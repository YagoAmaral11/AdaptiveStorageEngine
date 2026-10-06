using System.Text.Json;

namespace AdaptiveStorageEngine;

public class Program
{

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Informe o caminho do arquivo JSONL como primeiro argumento.");
            return 1;
        }

        try
        {
            using StreamReader input = File.OpenText(args[0]);

            string? line;
            while ((line = input.ReadLine()) is not null)
            {
                var request = ParseRequest(line);
                Engine.AddRequest(request);
            }

            Engine.ProcessQueue();
            return 0;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Não foi possível ler o arquivo de entrada: {exception.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"Não foi possível acessar o arquivo de entrada: {exception.Message}");
            return 1;
        }
    }

    private static Request ParseRequest(string line)
    {
        ulong id = 0;

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("O request deve ser um objeto JSON.");

            id = root.GetProperty("id").GetUInt64();
            string operationName = root.GetProperty("op").GetString() ?? throw new JsonException("A operação não pode ser nula.");

            if (!Enum.TryParse(operationName, ignoreCase: true, out OperationType operation))
                throw new JsonException($"Operação desconhecida: {operationName}.");

            return operation switch
            {
                OperationType.put => new PutRequest(
                    id,
                    operation,
                    root.GetProperty("key").GetUInt64(),
                    root.GetProperty("value").GetString() ?? throw new JsonException("O valor do request put não pode ser nulo.")),
                OperationType.get => new GetRequest(
                    id,
                    operation,
                    root.GetProperty("key").GetUInt64()),
                OperationType.delete => new RemoveRequest(
                    id,
                    operation,
                    root.GetProperty("key").GetUInt64()),
                OperationType.scan => new ScanRequest(
                    id,
                    operation,
                    root.GetProperty("start").GetUInt64(),
                    root.GetProperty("end").GetUInt64()),
                _ => throw new JsonException($"Operação desconhecida: {operationName}.")
            };
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            return new ErroredRequest(id, OperationType.error, exception.Message, line);
        }
    }   

}
