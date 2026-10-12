using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AdaptiveStorageEngine;

public static class Engine
{
    public const string DatabaseFileName = "database.dat";
    private static readonly int pageSize = 4096; // 2 ^ 12


    private static readonly Queue<Request> RequestQueue = [];
    private static readonly object PageFileHandleLock = new();
    private static readonly SemaphoreSlim PageAllocationLock = new(1, 1);
    private static readonly SemaphoreSlim MetadataLock = new(1, 1);
    private static SafeFileHandle? pageFileHandle;

    private static int queueCapacity = 10;    


    private static Metadata metadata = new(); // A metadata da database

    private static Dictionary<ulong, Page> PageCache = []; // Uma cache das páginas carregadas
    private static Dictionary<ulong, string> ValueCache = []; // A cache com os valores carregados    


    public static int QueueCapacity => queueCapacity;


    public static async Task Initialize(CancellationToken cancellationToken = default)
    {
        if (File.Exists(DatabaseFileName))
        {
            metadata = await LoadMetadataAsync(cancellationToken);
        }
        else
        {
            await CreateDatabaseFileAsync(cancellationToken);

            metadata = new Metadata
            {
                nextPageId = 0,
                pageTableSize = 0,
                pageTable = []
            };
        }                        
    }

    public static async Task<ulong> CreateEmptyPageAsync(CancellationToken cancellationToken = default)
    {
        await PageAllocationLock.WaitAsync(cancellationToken);

        try
        {
            SafeFileHandle handle = GetPageFileHandle();
            long fileLength = RandomAccess.GetLength(handle);

            if (fileLength % pageSize != 0)
                throw new InvalidDataException("O tamanho do arquivo de database não é múltiplo do tamanho da página.");

            ulong pageId = checked((ulong) (fileLength / pageSize));
            long pageOffset = fileLength;
            RandomAccess.SetLength(handle, checked(fileLength + pageSize));

            byte[] emptyPage = new byte[pageSize];
            await RandomAccess.WriteAsync(handle, emptyPage.AsMemory(), pageOffset, cancellationToken);

            return pageId;
        }
        finally
        {
            PageAllocationLock.Release();
        }
    }

    private static async Task InsertPageTableEntryAsync(ulong informationId, ulong firstPageId, CancellationToken cancellationToken = default)
    {
        const int pagePointerSize = sizeof(ulong);
        const int firstPageHeaderSize = sizeof(ulong) + sizeof(uint);
        const int entrySize = sizeof(ulong) * 2;
        const int firstPageEntriesPerPage = (4096 - firstPageHeaderSize) / entrySize;
        const int continuationPageEntriesPerPage = (4096 - pagePointerSize) / entrySize;

        await MetadataLock.WaitAsync(cancellationToken);

        try
        {
            if (metadata.pageTable.ContainsKey(informationId))
                throw new InvalidOperationException($"A informação com ID {informationId} já existe.");

            ulong currentPageId = 0;
            uint entriesBeforeCurrentPage = 0;
            bool firstPage = true;
            var visitedPages = new HashSet<ulong>();

            while (true)
            {
                if (!visitedPages.Add(currentPageId))
                    throw new InvalidDataException("Foi encontrado um ciclo inválido na cadeia da metadata.");

                byte[] currentPage = await ReadPageAsync(currentPageId, pageSize, cancellationToken);
                ulong nextPageId = BinaryPrimitives.ReadUInt64LittleEndian(currentPage.AsSpan(0, pagePointerSize));
                int contentOffset = firstPage ? firstPageHeaderSize : pagePointerSize;
                int entriesPerCurrentPage = firstPage ? firstPageEntriesPerPage : continuationPageEntriesPerPage;
                uint entriesInCurrentPage = metadata.pageTableSize - entriesBeforeCurrentPage;

                if (entriesInCurrentPage < entriesPerCurrentPage)
                {
                    int entryOffset = checked(contentOffset + (int)entriesInCurrentPage * entrySize);
                    await WritePageTableEntryAsync(currentPageId, entryOffset, informationId, firstPageId, cancellationToken);
                    break;
                }

                if (nextPageId == currentPageId)
                {
                    ulong newPageId = await CreateEmptyPageAsync(cancellationToken);
                    byte[] newPage = new byte[pageSize];
                    BinaryPrimitives.WriteUInt64LittleEndian(newPage.AsSpan(0, pagePointerSize), newPageId);
                    WritePageTableEntry(newPage, pagePointerSize, informationId, firstPageId);

                    long newPageOffset = checked((long)newPageId * pageSize);
                    await RandomAccess.WriteAsync(GetPageFileHandle(), newPage.AsMemory(), newPageOffset, cancellationToken);

                    byte[] nextPageBytes = new byte[pagePointerSize];
                    BinaryPrimitives.WriteUInt64LittleEndian(nextPageBytes, newPageId);
                    long currentPageOffset = checked((long)currentPageId * pageSize);
                    await RandomAccess.WriteAsync(GetPageFileHandle(), nextPageBytes.AsMemory(), currentPageOffset, cancellationToken);

                    if (currentPageId == 0)
                        metadata.nextPageId = newPageId;

                    break;
                }

                entriesBeforeCurrentPage = checked(entriesBeforeCurrentPage + (uint)entriesPerCurrentPage);
                currentPageId = nextPageId;
                firstPage = false;
            }

            uint newPageTableSize = checked(metadata.pageTableSize + 1);
            byte[] sizeBytes = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, newPageTableSize);
            await RandomAccess.WriteAsync(GetPageFileHandle(), sizeBytes.AsMemory(), sizeof(ulong), cancellationToken);

            metadata.pageTable.Add(informationId, firstPageId);
            metadata.pageTableSize = newPageTableSize;
        }
        finally
        {
            MetadataLock.Release();
        }
    }

    private static async Task WritePageTableEntryAsync(ulong pageId, int offset, ulong informationId, ulong firstPageId, CancellationToken cancellationToken)
    {
        byte[] entry = new byte[sizeof(ulong) * 2];
        WritePageTableEntry(entry, 0, informationId, firstPageId);
        long fileOffset = checked((long)pageId * pageSize + offset);
        await RandomAccess.WriteAsync(GetPageFileHandle(), entry.AsMemory(), fileOffset, cancellationToken);
    }

    private static void WritePageTableEntry(byte[] page, int offset, ulong informationId, ulong firstPageId)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(offset, sizeof(ulong)), informationId);
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(offset + sizeof(ulong), sizeof(ulong)), firstPageId);
    }

    public static async Task<ulong> CreateInformationAsync(ulong informationId, string information, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(information);

        const int pagePointerSize = sizeof(ulong);
        int contentSize = pageSize - pagePointerSize;
        byte[] informationBytes = Encoding.UTF8.GetBytes(information);
        int pageCount = informationBytes.Length == 0
            ? 1
            : checked((int)(((long)informationBytes.Length + contentSize - 1) / contentSize));

        ulong[] pageIds = new ulong[pageCount];
        for (int pageIndex = 0; pageIndex < pageIds.Length; pageIndex++)
            pageIds[pageIndex] = await CreateEmptyPageAsync(cancellationToken);

        SafeFileHandle handle = GetPageFileHandle();
        int informationOffset = 0;

        for (int pageIndex = 0; pageIndex < pageIds.Length; pageIndex++)
        {
            byte[] page = new byte[pageSize];
            ulong nextPageId = pageIndex + 1 < pageIds.Length
                ? pageIds[pageIndex + 1]
                : pageIds[pageIndex];

            BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(0, pagePointerSize), nextPageId);

            int bytesToCopy = Math.Min(contentSize, informationBytes.Length - informationOffset);
            if (bytesToCopy > 0)
            {
                informationBytes.AsSpan(informationOffset, bytesToCopy)
                    .CopyTo(page.AsSpan(pagePointerSize, bytesToCopy));
                informationOffset += bytesToCopy;
            }

            long pageOffset = checked((long)pageIds[pageIndex] * pageSize);
            await RandomAccess.WriteAsync(handle, page.AsMemory(), pageOffset, cancellationToken);
        }

        await InsertPageTableEntryAsync(informationId, pageIds[0], cancellationToken);
        return pageIds[0];
    }

    public static ulong GetInformationFirstPageId(ulong informationId)
    {
        if (!metadata.pageTable.TryGetValue(informationId, out ulong firstPageId))
            throw new KeyNotFoundException($"A informação com ID {informationId} não foi encontrada na pageTable.");

        return firstPageId;
    }

    public static async Task<string> ReadInformationAsync(ulong firstPageId, CancellationToken cancellationToken = default)
    {
        const int pagePointerSize = sizeof(ulong);
        var informationBytes = new List<byte>();
        var visitedPages = new HashSet<ulong>();
        ulong currentPageId = firstPageId;

        while (true)
        {
            if (!visitedPages.Add(currentPageId))
                throw new InvalidDataException("Foi encontrado um ciclo inválido nas páginas da informação.");

            byte[] page = await ReadPageAsync(currentPageId, pageSize, cancellationToken);
            ulong nextPageId = BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(0, pagePointerSize));
            informationBytes.AddRange(page.AsSpan(pagePointerSize).ToArray());

            if (nextPageId == currentPageId)
                break;

            currentPageId = nextPageId;
        }

        int contentLength = informationBytes.Count;
        while (contentLength > 0 && informationBytes[contentLength - 1] == 0)
            contentLength--;

        return Encoding.UTF8.GetString(informationBytes.ToArray(), 0, contentLength);
    }

    public static void AddRequest(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);

        RequestQueue.Enqueue(request);

        if (RequestQueue.Count >= QueueCapacity)
            ProcessQueue();        
    }

    public static void ProcessQueue()
    {
        throw new NotImplementedException();

        while (RequestQueue.Count > 0)
        {
            RequestQueue.Dequeue();
        }
    }

    

    /// <summary>
    /// Carrega da memória secundária os metadados da database, incluindo a tabela de páginas
    /// </summary>    
    /// <returns>A metadata carregada</returns>
    /// <exception cref="InvalidDataException">Retorna erro caso seja detectado erro no arquivo de database</exception>
    private static async Task<Metadata> LoadMetadataAsync(CancellationToken cancellationToken = default)
    {
        const ulong metadataPageId = 0;
        const int metadataHeaderSize = sizeof(ulong) + sizeof(uint);
        const int pagePointerSize = sizeof(ulong);
        const int pairSize = sizeof(ulong) * 2;

        byte[] firstPage = await ReadPageAsync(metadataPageId, pageSize, cancellationToken);
        
        ulong firstNextPageId = BinaryPrimitives.ReadUInt64LittleEndian(firstPage.AsSpan(0, sizeof(ulong)));
        uint pageTableSize = BinaryPrimitives.ReadUInt32LittleEndian(firstPage.AsSpan(sizeof(ulong), sizeof(uint)));

        var loadedMetadata = new Metadata
        {
            nextPageId = firstNextPageId,
            pageTableSize = pageTableSize
        };

        var visitedPages = new HashSet<ulong>();
        byte[] pairBuffer = new byte[pairSize];
        int pairBufferLength = 0;
        uint entriesRead = 0;
        ulong currentPageId = metadataPageId;
        byte[] currentPage = firstPage;

        while (entriesRead < pageTableSize)
        {
            if (!visitedPages.Add(currentPageId))
                throw new InvalidDataException("Foi encontrado um ciclo nas páginas da metadata antes do fim da pageTable.");

            ulong nextMetadataPageId = BinaryPrimitives.ReadUInt64LittleEndian(currentPage.AsSpan(0, pagePointerSize));
            bool isFirstMetadataPage = currentPageId == metadataPageId;
            int contentOffset = isFirstMetadataPage ? metadataHeaderSize : pagePointerSize;

            for (int offset = contentOffset; offset < currentPage.Length && entriesRead < pageTableSize; offset++)
            {
                pairBuffer[pairBufferLength++] = currentPage[offset];

                if (pairBufferLength != pairSize)
                    continue;

                ulong dataId = BinaryPrimitives.ReadUInt64LittleEndian(pairBuffer.AsSpan(0, sizeof(ulong)));
                ulong dataFirstPageId = BinaryPrimitives.ReadUInt64LittleEndian(pairBuffer.AsSpan(sizeof(ulong), sizeof(ulong)));
                loadedMetadata.pageTable.Add(dataId, dataFirstPageId);
                entriesRead++;
                pairBufferLength = 0;
            }

            if (entriesRead == pageTableSize)
                break;

            if (nextMetadataPageId == currentPageId)
                throw new InvalidDataException("A página da metadata aponta para si mesma antes do fim da pageTable.");

            currentPageId = nextMetadataPageId;
            currentPage = await ReadPageAsync(currentPageId, pageSize, cancellationToken);
        }

        if (pairBufferLength != 0)
            throw new InvalidDataException("A pageTable da metadata está truncada.");

        return loadedMetadata;
    }



    /// <summary>
    /// Cria um novo arquivo de database
    /// </summary>    
    /// <returns>Retorna verdadeiro se o arquivo foi criado com sucesso</returns>
    private static async Task<bool> CreateDatabaseFileAsync(CancellationToken cancellationToken = default)
    {
        SafeFileHandle handle = GetPageFileHandle();

        byte[] metadataPage = new byte[pageSize];
        BinaryPrimitives.WriteUInt64LittleEndian(metadataPage.AsSpan(0, sizeof(ulong)), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(metadataPage.AsSpan(sizeof(ulong), sizeof(uint)), 0);

        await RandomAccess.WriteAsync(handle, metadataPage.AsMemory(), 0, cancellationToken);
        return true;
    }

    /// <summary>
    /// Método para ler uma página do arquivo de database
    /// </summary>
    /// <param name="pageId">ID da página que está sendo buscada</param>
    /// <param name="pageSize">O tamanho de todas as páginas do arquivo de database</param>    
    /// <returns>Bytes da página</returns>
    /// <exception cref="ArgumentOutOfRangeException">Se a página não for válida</exception>
    public static async Task<byte[]> ReadPageAsync(ulong pageId, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        ulong offsetValue;

        try
        {
            offsetValue = checked(pageId * (ulong) pageSize);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(pageId), "O offset da página excede o limite suportado.");
        }

        if (offsetValue > long.MaxValue || offsetValue > (ulong) (long.MaxValue - pageSize))
        {
            throw new ArgumentOutOfRangeException(nameof(pageId), "O offset da página excede o limite suportado.");
        }

        SafeFileHandle handle = GetPageFileHandle();

        byte[] page = new byte[pageSize];
        long offset = (long) offsetValue;
        int bytesRead = 0;

        while (bytesRead < page.Length)
        {
            int read = await RandomAccess.ReadAsync(handle, page.AsMemory(bytesRead), offset + bytesRead, cancellationToken);

            if (read == 0)
            {
                break;
            }

            bytesRead += read;
        }

        return page;
    }

    private static SafeFileHandle GetPageFileHandle()
    {
        if (pageFileHandle is not null)
        {
            return pageFileHandle;
        }
        else
        {
            CreatePageFileHandle();
            return pageFileHandle;
        }        
    }

    private static void CreatePageFileHandle(string filePath = DatabaseFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        lock (PageFileHandleLock)
        {
            pageFileHandle?.Dispose();
            pageFileHandle = File.OpenHandle(
                filePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                FileOptions.Asynchronous);
        }
    }
        
    // TODO: Função para apagar um informação de um ID, continuamente apagando todas as páginas dessa informação
    // TODO: Guardar uma tabela com todas as páginas que não estão sendo usadas atualmente para guardar informações, e quando criar novas páginas
    //       puxar a primeira página zerada marcada como livre
    // TODO: Implementar uma cache à nível de informação, mantendo as informações mais acessadas na memória principal. Se essa memória for atualizada,
    //       atualizar ela na memória secundária também
    // TODO: Implementar uma cache à nível de páginas, mantendo as páginas mais acessadas na memória principal. Se essa página for atualizada, 
    //       atualizar ela na memória secundária também    


}
