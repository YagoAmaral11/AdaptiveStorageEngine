namespace AdaptiveStorageEngine;

public static class Engine
{
    private static readonly Queue<Request> RequestQueue = [];

    private static int queueCapacity = 10;
    public static int QueueCapacity => queueCapacity;


    private static readonly int pageSize = 4096; // 2 ^ 12

    private static int medatadaPageAmmount; // a quantidade de páginas que os metadados (incluindo a tabela de páginas) ocupa
    private static Dictionary<ulong, ulong> pageTable = []; // A tabela de páginas; O ID do elemento guardado aponta para o ID da primeira página desse elemento

    private static Dictionary<ulong, Page> PageCache = []; // Uma cache das páginas carregadas
    private static Dictionary<ulong, string> ValueCache = []; // A cache com os valores carregados


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

}
