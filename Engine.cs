using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AdaptiveStorageEngine;

public static class Engine
{
    public const string DatabaseFileName = "database.dat";
    private const int PageSize = 4096; // 2 ^ 12
    private const int PageTableEntrySize = sizeof(ulong) * 2;
    private const int TombstoneEntrySize = sizeof(ulong);

    private static readonly Queue<Request> RequestQueue = [];
    private static int queueCapacity = 10;    
    public static int QueueCapacity => queueCapacity;

    private static readonly object PageFileHandleLock = new();
    private static readonly SemaphoreSlim PageAllocationLock = new(1, 1);
    private static readonly SemaphoreSlim MetadataLock = new(1, 1);
    private static SafeFileHandle? pageFileHandle;        

    private static Metadata metadata = new();

    // TODO: Implementar o ProcessQueue para processar os diferentes tipos de request 
    // TODO: Implementar um método para escrever o arquivo final de resposta para todos os requests (.jsonl de respostas de todos os requests)
    // TODO: PROXIMAS ETAPAS -> Implementar uma cache à nível de informação, mantendo as informações mais acessadas na memória principal. Se essa memória for atualizada,
    //       atualizar ela na memória secundária também
    // TODO: PROXIMAS ETAPAS -> Implementar uma cache à nível de páginas, mantendo as páginas mais acessadas na memória principal. Se essa página for atualizada, 
    //       atualizar ela na memória secundária também    


    /// <summary>Inicializa o arquivo, o header da metadata e as duas tabelas em memória.</summary>
    /// <param name="cancellationToken">Token usado para cancelar a leitura.</param>
    /// <exception cref="IOException">Lançada quando o arquivo não pode ser criado ou lido.</exception>
    /// <exception cref="InvalidDataException">Lançada quando o layout do arquivo é inválido.</exception>
    public static async Task Initialize(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(DatabaseFileName))
            await CreateDatabaseFileAsync(cancellationToken);

        metadata = await LoadMetadataAsync(cancellationToken);
    }

    /// <summary>Salva as tabelas em memória no arquivo e fecha o handle do database.</summary>
    /// <param name="cancellationToken">Token usado para cancelar a gravação.</param>
    /// <exception cref="IOException">Lançada quando as tabelas não podem ser gravadas.</exception>
    public static async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await SaveTablesAsync(cancellationToken);
        lock (PageFileHandleLock)
        {
            pageFileHandle?.Dispose();
            pageFileHandle = null;
        }
    }

    /// <summary>Adiciona uma requisição à fila e processa a fila quando sua capacidade é atingida.</summary>
    /// <param name="request">Requisição a ser adicionada.</param>
    /// <exception cref="ArgumentNullException">Lançada quando a requisição é nula.</exception>
    public static void AddRequest(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequestQueue.Enqueue(request);
        if (RequestQueue.Count >= QueueCapacity)
            ProcessQueue();
    }

    /// <summary>Processa as requisições pendentes.</summary>
    /// <exception cref="NotImplementedException">Lançada enquanto o processador de requisições não estiver implementado.</exception>
    public static void ProcessQueue() => throw new NotImplementedException();

    /// <summary>Cria uma página zerada no final do arquivo e retorna seu ID.</summary>
    /// <param name="cancellationToken">Token usado para cancelar a operação.</param>
    /// <returns>ID da página criada.</returns>
    /// <exception cref="InvalidDataException">Lançada quando o tamanho do arquivo não é múltiplo do tamanho da página.</exception>
    public static async Task<ulong> CreateEmptyPageAsync(CancellationToken cancellationToken = default)
    {
        await PageAllocationLock.WaitAsync(cancellationToken);
        try
        {
            return await AppendEmptyPageUnsafeAsync(cancellationToken);
        }
        finally
        {
            PageAllocationLock.Release();
        }
    }

    /// <summary>Cria uma informação, reutilizando tombstones antes de anexar páginas novas.</summary>
    /// <param name="informationId">ID lógico da informação.</param>
    /// <param name="information">Texto a ser armazenado em UTF-8.</param>
    /// <param name="cancellationToken">Token usado para cancelar a operação.</param>
    /// <returns>ID da primeira página física da informação.</returns>
    /// <exception cref="InvalidOperationException">Lançada quando o ID já existe.</exception>
    public static async Task<ulong> CreateInformationAsync(ulong informationId, string information, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(information);
        await MetadataLock.WaitAsync(cancellationToken);
        try
        {
            if (metadata.pageTable.ContainsKey(informationId))
                throw new InvalidOperationException($"A informação com ID {informationId} já existe.");

            byte[] bytes = Encoding.UTF8.GetBytes(information);
            int contentSize = PageSize - Page.HeaderSize;
            int pageCount = Math.Max(1, checked((int)(((long)bytes.Length + contentSize - 1) / contentSize)));
            List<ulong> pageIds = await AllocatePagesUnsafeAsync(pageCount, cancellationToken);

            int offset = 0;
            for (int index = 0; index < pageIds.Count; index++)
            {
                byte[] page = new byte[PageSize];
                ulong nextPageId = index + 1 < pageIds.Count ? pageIds[index + 1] : pageIds[index];
                BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(0, Page.HeaderSize), nextPageId);
                int count = Math.Min(contentSize, bytes.Length - offset);
                if (count > 0)
                {
                    bytes.AsSpan(offset, count).CopyTo(page.AsSpan(Page.HeaderSize, count));
                    offset += count;
                }
                await WritePageAsync(pageIds[index], page, cancellationToken);
            }

            metadata.pageTable.Add(informationId, pageIds[0]);
            metadata.pageTableSize++;
            return pageIds[0];
        }
        finally
        {
            MetadataLock.Release();
        }
    }

    /// <summary>Obtém a primeira página física associada a um ID lógico.</summary>
    /// <param name="informationId">ID lógico consultado.</param>
    /// <returns>ID da primeira página da informação.</returns>
    /// <exception cref="KeyNotFoundException">Lançada quando o ID não está na pageTable.</exception>
    public static ulong GetInformationFirstPageId(ulong informationId)
    {
        if (!metadata.pageTable.TryGetValue(informationId, out ulong pageId))
            throw new KeyNotFoundException($"A informação com ID {informationId} não foi encontrada.");
        return pageId;
    }

    /// <summary>Lê todas as páginas encadeadas de uma informação e reconstrói seu texto UTF-8.</summary>
    /// <param name="firstPageId">ID da primeira página da informação.</param>
    /// <param name="cancellationToken">Token usado para cancelar a leitura.</param>
    /// <returns>Texto armazenado.</returns>
    /// <exception cref="InvalidDataException">Lançada quando a cadeia contém um ciclo inválido.</exception>
    public static async Task<string> ReadInformationAsync(ulong firstPageId, CancellationToken cancellationToken = default)
    {
        var bytes = new List<byte>();
        var visited = new HashSet<ulong>();
        ulong current = firstPageId;
        while (true)
        {
            if (!visited.Add(current))
                throw new InvalidDataException("Foi encontrado um ciclo inválido nas páginas da informação.");
            byte[] page = await ReadPageAsync(current, PageSize, cancellationToken);
            ulong next = BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(0, Page.HeaderSize));
            bytes.AddRange(page.AsSpan(Page.HeaderSize).ToArray());
            if (next == current)
                break;
            current = next;
        }
        int length = bytes.Count;
        while (length > 0 && bytes[length - 1] == 0)
            length--;
        return Encoding.UTF8.GetString(bytes.ToArray(), 0, length);
    }

    /// <summary>Remove uma informação e marca todas as suas páginas como tombstones.</summary>
    /// <param name="informationId">ID lógico da informação a remover.</param>
    /// <param name="cancellationToken">Token usado para cancelar a leitura das páginas.</param>
    /// <returns>Indica se a informação existia e foi removida.</returns>
    /// <exception cref="InvalidDataException">Lançada quando a cadeia da informação é inválida.</exception>
    public static async Task<bool> DeleteInformationAsync(ulong informationId, CancellationToken cancellationToken = default)
    {
        await MetadataLock.WaitAsync(cancellationToken);
        try
        {
            if (!metadata.pageTable.Remove(informationId, out ulong firstPageId))
                return false;
            metadata.pageTableSize--;

            var visited = new HashSet<ulong>();
            ulong current = firstPageId;
            while (true)
            {
                if (!visited.Add(current))
                    throw new InvalidDataException("Foi encontrado um ciclo inválido nas páginas da informação.");
                byte[] page = await ReadPageAsync(current, PageSize, cancellationToken);
                ulong next = BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(0, Page.HeaderSize));
                DeletePage(current);
                if (next == current)
                    break;
                current = next;
            }
            return true;
        }
        finally
        {
            MetadataLock.Release();
        }
    }

    /// <summary>Marca uma página como livre na tombstone table e atualiza o índice de intervalos.</summary>
    /// <param name="pageId">ID da página liberada.</param>
    /// <summary>Adiciona uma página liberada à tombstone table em memória.</summary>
    /// <param name="pageId">ID da página que deixou de ser usada.</param>
    private static void DeletePage(ulong pageId)
    {
        int index = metadata.tombstoneTable.BinarySearch(pageId);
        if (index < 0)
        {
            metadata.tombstoneTable.Insert(~index, pageId);
            metadata.tombstoneTableSize++;
            metadata.freePageIntervals.AddPage(pageId);
        }
    }

    /// <summary>Salva a pageTable e a tombstoneTable, atualizando o header da página 0.</summary>
    /// <param name="cancellationToken">Token usado para cancelar a gravação.</param>
    /// <exception cref="InvalidDataException">Lançada quando uma tabela não pode ser serializada.</exception>
    public static async Task SaveTablesAsync(CancellationToken cancellationToken = default)
    {
        await MetadataLock.WaitAsync(cancellationToken);
        try
        {
            List<ulong> oldPageTablePages = await ReadTablePageIdsAsync(metadata.pageTablePageId, metadata.pageTableSize, PageTableEntrySize, cancellationToken);
            int pageTableCapacity = (PageSize - Page.HeaderSize) / PageTableEntrySize;
            int requiredPageTablePages = Math.Max(1, (int)(((long)metadata.pageTableSize + pageTableCapacity - 1) / pageTableCapacity));
            while (oldPageTablePages.Count < requiredPageTablePages)
                oldPageTablePages.Add(await AppendEmptyPageUnsafeAsync(cancellationToken));
            for (int i = requiredPageTablePages; i < oldPageTablePages.Count; i++)
                DeletePage(oldPageTablePages[i]);
            oldPageTablePages = oldPageTablePages.Take(requiredPageTablePages).ToList();
            await WritePageTableAsync(oldPageTablePages, cancellationToken);
            metadata.pageTablePageId = oldPageTablePages[0];

            List<ulong> oldTombstonePages = await ReadTablePageIdsAsync(metadata.tombstoneTablePageId, metadata.tombstoneTableSize, TombstoneEntrySize, cancellationToken);
            int tombstoneCapacity = (PageSize - Page.HeaderSize) / TombstoneEntrySize;
            int requiredTombstonePages = Math.Max(1, (int)(((long)metadata.tombstoneTableSize + tombstoneCapacity - 1) / tombstoneCapacity));
            for (int i = requiredTombstonePages; i < oldTombstonePages.Count; i++)
                DeletePage(oldTombstonePages[i]);
            requiredTombstonePages = Math.Max(1, (int)(((long)metadata.tombstoneTableSize + tombstoneCapacity - 1) / tombstoneCapacity));
            while (oldTombstonePages.Count < requiredTombstonePages)
                oldTombstonePages.Add(await AppendEmptyPageUnsafeAsync(cancellationToken));
            oldTombstonePages = oldTombstonePages.Take(requiredTombstonePages).ToList();
            await WriteTombstoneTableAsync(oldTombstonePages, cancellationToken);
            metadata.tombstoneTablePageId = oldTombstonePages[0];

            await WriteMetadataHeaderAsync(cancellationToken);
        }
        finally
        {
            MetadataLock.Release();
        }
    }

    /// <summary>Lê uma página do arquivo em um offset calculado pelo ID e tamanho informado.</summary>
    /// <param name="pageId">ID da página.</param>
    /// <param name="pageSize">Tamanho da página em bytes.</param>
    /// <param name="cancellationToken">Token usado para cancelar a leitura.</param>
    /// <returns>Bytes lidos, preenchidos com zero quando o arquivo termina antes da página.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Lançada para tamanho ou ID que excedam os offsets suportados.</exception>
    public static async Task<byte[]> ReadPageAsync(ulong pageId, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        ulong offsetValue = checked(pageId * (ulong)pageSize);
        if (offsetValue > long.MaxValue || offsetValue > (ulong)(long.MaxValue - pageSize))
            throw new ArgumentOutOfRangeException(nameof(pageId));
        byte[] page = new byte[pageSize];
        int readBytes = 0;
        while (readBytes < page.Length)
        {
            int read = await RandomAccess.ReadAsync(GetPageFileHandle(), page.AsMemory(readBytes), (long)offsetValue + readBytes, cancellationToken);
            if (read == 0)
                break;
            readBytes += read;
        }
        return page;
    }

    /// <summary>Carrega o header e as duas tabelas persistidas no arquivo.</summary>
    /// <param name="cancellationToken">Token usado para cancelar a leitura.</param>
    /// <returns>Metadata reconstruída em memória.</returns>
    /// <exception cref="InvalidDataException">Lançada quando alguma tabela possui formato inválido.</exception>
    private static async Task<Metadata> LoadMetadataAsync(CancellationToken cancellationToken)
    {
        byte[] header = await ReadPageAsync(0, PageSize, cancellationToken);
        var loaded = new Metadata
        {
            pageTablePageId = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0, sizeof(ulong))),
            pageTableSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(sizeof(ulong), sizeof(uint))),
            tombstoneTablePageId = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(sizeof(ulong) + sizeof(uint), sizeof(ulong))),
            tombstoneTableSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(sizeof(ulong) * 2 + sizeof(uint), sizeof(uint)))
        };
        loaded.pageTable = await LoadPageTableAsync(loaded.pageTablePageId, loaded.pageTableSize, cancellationToken);
        loaded.tombstoneTable = await LoadTombstoneTableAsync(loaded.tombstoneTablePageId, loaded.tombstoneTableSize, cancellationToken);
        loaded.freePageIntervals.Rebuild(loaded.tombstoneTable);
        return loaded;
    }

    /// <summary>Cria o arquivo inicial com o header e as páginas reservadas das tabelas.</summary>
    /// <param name="cancellationToken">Token usado para cancelar a criação.</param>
    private static async Task CreateDatabaseFileAsync(CancellationToken cancellationToken)
    {
        CreatePageFileHandle(FileMode.CreateNew);
        RandomAccess.SetLength(GetPageFileHandle(), (long)PageSize * 3);
        byte[] header = new byte[PageSize];
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0, sizeof(ulong)), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(sizeof(ulong), sizeof(uint)), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(sizeof(ulong) + sizeof(uint), sizeof(ulong)), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(sizeof(ulong) * 2 + sizeof(uint), sizeof(uint)), 0);
        await WritePageAsync(0, header, cancellationToken);
        await WriteTablePagePointerAsync(1, 1, cancellationToken);
        await WriteTablePagePointerAsync(2, 2, cancellationToken);
    }

    /// <summary>Seleciona páginas livres ou anexa novas páginas sem adquirir novamente o lock de alocação.</summary>
    /// <param name="count">Quantidade de páginas necessária.</param>
    /// <param name="cancellationToken">Token usado para cancelar a operação.</param>
    /// <returns>IDs das páginas selecionadas.</returns>
    /// <exception cref="InvalidOperationException">Lançada quando não há páginas livres suficientes para a seleção.</exception>
    private static async Task<List<ulong>> AllocatePagesUnsafeAsync(int count, CancellationToken cancellationToken)
    {
        await PageAllocationLock.WaitAsync(cancellationToken);
        try
        {
            List<ulong> ids;
            if (!metadata.freePageIntervals.TryTakeContiguous(count, out ids))
            {
                ids = metadata.tombstoneTable.Count >= count
                    ? metadata.freePageIntervals.TakeAny(count)
                    : metadata.tombstoneTable.ToList();
                if (ids.Count < count)
                {
                    int missing = count - ids.Count;
                    for (int i = 0; i < missing; i++)
                        ids.Add(await AppendEmptyPageUnsafeAsync(cancellationToken));
                }
            }
            foreach (ulong id in ids)
            {
                int index = metadata.tombstoneTable.BinarySearch(id);
                if (index >= 0)
                    metadata.tombstoneTable.RemoveAt(index);
            }
            metadata.tombstoneTableSize = (uint)metadata.tombstoneTable.Count;
            metadata.freePageIntervals.Rebuild(metadata.tombstoneTable);
            return ids;
        }
        finally
        {
            PageAllocationLock.Release();
        }
    }

    /// <summary>Anexa uma página zerada sem adquirir novamente o lock de alocação.</summary>
    /// <param name="cancellationToken">Token usado para cancelar a escrita.</param>
    /// <returns>ID da página anexada.</returns>
    /// <exception cref="InvalidDataException">Lançada quando o tamanho do arquivo não é múltiplo do tamanho da página.</exception>
    private static async Task<ulong> AppendEmptyPageUnsafeAsync(CancellationToken cancellationToken)
    {
        SafeFileHandle handle = GetPageFileHandle();
        long length = RandomAccess.GetLength(handle);
        if (length % PageSize != 0)
            throw new InvalidDataException("O tamanho do arquivo não é múltiplo do tamanho da página.");
        ulong pageId = checked((ulong)(length / PageSize));
        RandomAccess.SetLength(handle, checked(length + PageSize));
        await WritePageAsync(pageId, new byte[PageSize], cancellationToken);
        return pageId;
    }

    /// <summary>Grava uma página completa no offset correspondente ao seu ID.</summary>
    /// <param name="pageId">ID da página.</param>
    /// <param name="page">Buffer completo da página.</param>
    /// <param name="cancellationToken">Token usado para cancelar a escrita.</param>
    private static async Task WritePageAsync(ulong pageId, byte[] page, CancellationToken cancellationToken)
    {
        long offset = checked((long)pageId * PageSize);
        await RandomAccess.WriteAsync(GetPageFileHandle(), page.AsMemory(), offset, cancellationToken);
    }

    /// <summary>Grava somente o ponteiro de próxima página de uma página de tabela.</summary>
    /// <param name="pageId">Página cujo ponteiro será gravado.</param>
    /// <param name="nextPageId">ID da próxima página ou da própria página.</param>
    /// <param name="cancellationToken">Token usado para cancelar a escrita.</param>
    private static async Task WriteTablePagePointerAsync(ulong pageId, ulong nextPageId, CancellationToken cancellationToken)
    {
        byte[] pointer = new byte[Page.HeaderSize];
        BinaryPrimitives.WriteUInt64LittleEndian(pointer, nextPageId);
        await RandomAccess.WriteAsync(GetPageFileHandle(), pointer.AsMemory(), checked((long)pageId * PageSize), cancellationToken);
    }

    /// <summary>Persiste o header da metadata na página zero.</summary>
    /// <param name="cancellationToken">Token usado para cancelar a escrita.</param>
    private static async Task WriteMetadataHeaderAsync(CancellationToken cancellationToken)
    {
        byte[] header = new byte[Metadata.HeaderSize];
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0, sizeof(ulong)), metadata.pageTablePageId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(sizeof(ulong), sizeof(uint)), metadata.pageTableSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(sizeof(ulong) + sizeof(uint), sizeof(ulong)), metadata.tombstoneTablePageId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(sizeof(ulong) * 2 + sizeof(uint), sizeof(uint)), metadata.tombstoneTableSize);
        await RandomAccess.WriteAsync(GetPageFileHandle(), header.AsMemory(), 0, cancellationToken);
    }

    /// <summary>Obtém todos os IDs da cadeia física de uma tabela.</summary>
    /// <param name="firstPageId">Primeira página da tabela.</param>
    /// <param name="size">Quantidade de entradas da tabela.</param>
    /// <param name="entrySize">Tamanho de cada entrada em bytes.</param>
    /// <param name="cancellationToken">Token usado para cancelar a leitura.</param>
    /// <returns>IDs das páginas físicas da tabela.</returns>
    /// <exception cref="InvalidDataException">Lançada quando a cadeia tem ciclo ou páginas insuficientes.</exception>
    private static async Task<List<ulong>> ReadTablePageIdsAsync(ulong firstPageId, uint size, int entrySize, CancellationToken cancellationToken)
    {
        int capacity = (PageSize - Page.HeaderSize) / entrySize;
        int requiredCount = Math.Max(1, (int)(((long)size + capacity - 1) / capacity));
        var ids = new List<ulong>();
        var visited = new HashSet<ulong>();
        ulong current = firstPageId;

        while (true)
        {
            if (!visited.Add(current))
                throw new InvalidDataException("A cadeia de páginas da tabela contém um ciclo inválido.");
            ids.Add(current);
            byte[] page = await ReadPageAsync(current, PageSize, cancellationToken);
            ulong next = BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(0, Page.HeaderSize));
            if (next == current)
                break;
            current = next;
        }

        if (ids.Count < requiredCount)
            throw new InvalidDataException("A tabela termina antes de armazenar todos os elementos.");
        return ids;
    }

    /// <summary>Carrega pares de IDs da pageTable a partir das páginas persistidas.</summary>
    /// <param name="firstPageId">Primeira página da pageTable.</param>
    /// <param name="size">Quantidade de pares esperados.</param>
    /// <param name="cancellationToken">Token usado para cancelar a leitura.</param>
    /// <returns>Dicionário reconstruído.</returns>
    private static async Task<Dictionary<ulong, ulong>> LoadPageTableAsync(ulong firstPageId, uint size, CancellationToken cancellationToken)
    {
        var result = new Dictionary<ulong, ulong>();
        List<ulong> pages = await ReadTablePageIdsAsync(firstPageId, size, PageTableEntrySize, cancellationToken);
        int remaining = checked((int)size);
        foreach (ulong pageId in pages)
        {
            byte[] page = await ReadPageAsync(pageId, PageSize, cancellationToken);
            int count = Math.Min(remaining, (PageSize - Page.HeaderSize) / PageTableEntrySize);
            for (int i = 0; i < count; i++)
            {
                int offset = Page.HeaderSize + i * PageTableEntrySize;
                result.Add(BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(offset, sizeof(ulong))), BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(offset + sizeof(ulong), sizeof(ulong))));
            }
            remaining -= count;
        }
        return result;
    }

    /// <summary>Carrega e valida os IDs ordenados da tombstone table.</summary>
    /// <param name="firstPageId">Primeira página da tombstone table.</param>
    /// <param name="size">Quantidade de IDs esperados.</param>
    /// <param name="cancellationToken">Token usado para cancelar a leitura.</param>
    /// <returns>Lista ordenada de IDs de páginas livres.</returns>
    /// <exception cref="InvalidDataException">Lançada quando a tabela não está ordenada.</exception>
    private static async Task<List<ulong>> LoadTombstoneTableAsync(ulong firstPageId, uint size, CancellationToken cancellationToken)
    {
        var result = new List<ulong>(checked((int)size));
        List<ulong> pages = await ReadTablePageIdsAsync(firstPageId, size, TombstoneEntrySize, cancellationToken);
        int remaining = checked((int)size);
        foreach (ulong pageId in pages)
        {
            byte[] page = await ReadPageAsync(pageId, PageSize, cancellationToken);
            int count = Math.Min(remaining, (PageSize - Page.HeaderSize) / TombstoneEntrySize);
            for (int i = 0; i < count; i++)
                result.Add(BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(Page.HeaderSize + i * TombstoneEntrySize, sizeof(ulong))));
            remaining -= count;
        }
        if (!result.SequenceEqual(result.OrderBy(id => id)))
            throw new InvalidDataException("A tombstoneTable não está ordenada.");
        return result;
    }

    /// <summary>Serializa a pageTable nas páginas físicas fornecidas.</summary>
    /// <param name="pages">Páginas que armazenarão a tabela.</param>
    /// <param name="cancellationToken">Token usado para cancelar a escrita.</param>
    private static async Task WritePageTableAsync(List<ulong> pages, CancellationToken cancellationToken)
    {
        int capacity = (PageSize - Page.HeaderSize) / PageTableEntrySize;
        for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            byte[] page = new byte[PageSize];
            ulong next = pageIndex + 1 < pages.Count ? pages[pageIndex + 1] : pages[pageIndex];
            BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(0, Page.HeaderSize), next);
            int first = pageIndex * capacity;
            int count = Math.Min(capacity, metadata.pageTable.Count - first);
            for (int i = 0; i < count; i++)
            {
                var pair = metadata.pageTable.ElementAt(first + i);
                int offset = Page.HeaderSize + i * PageTableEntrySize;
                BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(offset, sizeof(ulong)), pair.Key);
                BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(offset + sizeof(ulong), sizeof(ulong)), pair.Value);
            }
            await WritePageAsync(pages[pageIndex], page, cancellationToken);
        }
    }

    /// <summary>Serializa a tombstone table ordenada nas páginas físicas fornecidas.</summary>
    /// <param name="pages">Páginas que armazenarão a tabela.</param>
    /// <param name="cancellationToken">Token usado para cancelar a escrita.</param>
    private static async Task WriteTombstoneTableAsync(List<ulong> pages, CancellationToken cancellationToken)
    {
        int capacity = (PageSize - Page.HeaderSize) / TombstoneEntrySize;
        for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            byte[] page = new byte[PageSize];
            ulong next = pageIndex + 1 < pages.Count ? pages[pageIndex + 1] : pages[pageIndex];
            BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(0, Page.HeaderSize), next);
            int first = pageIndex * capacity;
            int count = Math.Min(capacity, metadata.tombstoneTable.Count - first);
            for (int i = 0; i < count; i++)
                BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(Page.HeaderSize + i * TombstoneEntrySize, sizeof(ulong)), metadata.tombstoneTable[first + i]);
            await WritePageAsync(pages[pageIndex], page, cancellationToken);
        }
    }

    /// <summary>Obtém o handle compartilhado do arquivo de database.</summary>
    /// <returns>Handle aberto para leitura e escrita assíncronas.</returns>
    private static SafeFileHandle GetPageFileHandle()
    {
        if (pageFileHandle is not null)
            return pageFileHandle;
        CreatePageFileHandle(FileMode.OpenOrCreate);
        return pageFileHandle!;
    }

    /// <summary>Cria ou abre o handle compartilhado usando o modo informado.</summary>
    /// <param name="mode">Modo de abertura do arquivo.</param>
    private static void CreatePageFileHandle(FileMode mode)
    {
        lock (PageFileHandleLock)
        {
            pageFileHandle?.Dispose();
            pageFileHandle = File.OpenHandle(DatabaseFileName, mode, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.Asynchronous);
        }
    }

}