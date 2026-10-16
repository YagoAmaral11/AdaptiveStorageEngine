using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;

namespace AdaptiveStorageEngine;


public enum OperationType
{
    put,
    get,
    delete,
    scan,
    error
}

public enum ResponseType
{
    ok,
    not_found,
    error
}

public record Request(ulong id, OperationType op);
public record PutRequest(ulong id, OperationType op, ulong key, string value) : Request(id, op);
public record GetRequest(ulong id, OperationType op, ulong key) : Request(id, op);
public record RemoveRequest(ulong id, OperationType op, ulong key) : Request(id, op);
public record ScanRequest(ulong id, OperationType op, ulong start, ulong end) : Request(id, op);
public record ErroredRequest(ulong id, OperationType op, string error, string rawJson) : Request(id, op);

public record SimpleResponse(ulong id, ResponseType response); // Usado nas respostas de Put e Remove
public record GetResponse(ulong id, ResponseType response, string? value);
public record ErorResponse(ulong id, ResponseType response, string error, string rawJson);
// TODO: Scan Request


public struct Page
{
    // OBS: A ordem dos membros aqui é a EXATA ordem em que os membros são serializados/deserializados da .db

    public ulong nextPageID;
    public string content;

    /// <summary>
    /// Tamanho, em bytes, do ponteiro para a próxima página nas páginas ordinárias.
    /// </summary>
    public static int HeaderSize => sizeof(ulong);
}

public class Metadata
{
    // OBS: A ordem dos membros aqui é a EXATA ordem em que os membros são serializados/deserializados da .db

    public ulong pageTablePageId; // o ponteiro para a primeira página da pageTable
    public uint pageTableSize; // o tamanho da pageTable, que é a quantidade do par (64 bits da chave + 64 bits do id da página)
    public ulong tombstoneTablePageId; // o ponteiro para a primeira página da tombstoneTable
    public uint tombstoneTableSize; // quantidade de IDs na tombstoneTable

    public Dictionary<ulong, ulong> pageTable = new Dictionary<ulong, ulong>(); // A tabela de páginas; O ID do elemento guardado aponta para o ID da primeira página desse elemento    
    public List<ulong> tombstoneTable = new List<ulong>(); // IDs das páginas que podem ser reutilizadas.
    public FreePageIntervalIndex freePageIntervals = new FreePageIntervalIndex();

    /// <summary>
    /// Tamanho, em bytes, do header da página de metadata.
    /// </summary>
    public static int HeaderSize => sizeof(ulong) + sizeof(uint) + sizeof(ulong) + sizeof(uint);
}

public readonly record struct FreePageInterval(ulong FirstPageId, ulong Length)
{
    /// <summary>
    /// Obtém o ID da última página do intervalo.
    /// </summary>
    public ulong LastPageId => checked(FirstPageId + Length - 1);
}

/// <summary>
/// Índice ordenado dos intervalos contíguos de páginas livres.
/// </summary>
public sealed class FreePageIntervalIndex
{
    private readonly SortedSet<FreePageInterval> intervals = new(
        Comparer<FreePageInterval>.Create((left, right) => left.FirstPageId.CompareTo(right.FirstPageId)));

    /// <summary>Remove todos os intervalos armazenados.</summary>
    public void Clear() => intervals.Clear();

    /// <summary>Adiciona uma página livre, unindo intervalos adjacentes.</summary>
    /// <param name="pageId">ID da página livre.</param>
    public void AddPage(ulong pageId)
    {
        FreePageInterval? previous = null;
        FreePageInterval? next = null;

        foreach (FreePageInterval interval in intervals)
        {
            if (interval.FirstPageId <= pageId && pageId <= interval.LastPageId)
                return;
            if (interval.LastPageId + 1 == pageId)
                previous = interval;
            if (interval.FirstPageId == pageId + 1)
            {
                next = interval;
                break;
            }
            if (interval.FirstPageId > pageId + 1)
            {
                next = interval;
                break;
            }
        }

        if (previous.HasValue && next.HasValue && next.Value.FirstPageId == pageId + 1)
        {
            intervals.Remove(previous.Value);
            intervals.Remove(next.Value);
            intervals.Add(new FreePageInterval(previous.Value.FirstPageId, next.Value.LastPageId - previous.Value.FirstPageId + 1));
        }
        else if (previous.HasValue)
        {
            intervals.Remove(previous.Value);
            intervals.Add(new FreePageInterval(previous.Value.FirstPageId, previous.Value.Length + 1));
        }
        else if (next.HasValue && next.Value.FirstPageId == pageId + 1)
        {
            intervals.Remove(next.Value);
            intervals.Add(new FreePageInterval(pageId, next.Value.Length + 1));
        }
        else
            intervals.Add(new FreePageInterval(pageId, 1));
    }

    /// <summary>Reconstrói o índice a partir de IDs de páginas livres.</summary>
    /// <param name="pageIds">IDs ordenados ou não ordenados das páginas livres.</param>
    public void Rebuild(IEnumerable<ulong> pageIds)
    {
        Clear();
        foreach (ulong pageId in pageIds.OrderBy(id => id))
            AddPage(pageId);
    }

    /// <summary>Obtém o primeiro intervalo que comporta a quantidade solicitada.</summary>
    /// <param name="pageCount">Quantidade de páginas desejada.</param>
    /// <param name="pageIds">IDs removidos do índice, quando houver um intervalo adequado.</param>
    /// <returns><see langword="true"/> quando encontrou um intervalo contíguo.</returns>
    public bool TryTakeContiguous(int pageCount, out List<ulong> pageIds)
    {
        foreach (FreePageInterval interval in intervals)
        {
            if (interval.Length < (ulong)pageCount)
                continue;
            pageIds = Enumerable.Range(0, pageCount).Select(index => interval.FirstPageId + (ulong)index).ToList();
            intervals.Remove(interval);
            if (interval.Length > (ulong)pageCount)
                intervals.Add(new FreePageInterval(interval.FirstPageId + (ulong)pageCount, interval.Length - (ulong)pageCount));
            return true;
        }
        pageIds = [];
        return false;
    }

    /// <summary>Obtém páginas livres dos menores IDs, possivelmente de vários intervalos.</summary>
    /// <param name="pageCount">Quantidade de páginas desejada.</param>
    /// <returns>IDs das páginas removidas do índice.</returns>
    /// <exception cref="InvalidOperationException">Lançada quando não há páginas suficientes.</exception>
    public List<ulong> TakeAny(int pageCount)
    {
        var pageIds = new List<ulong>(pageCount);
        while (pageIds.Count < pageCount)
        {
            if (intervals.Count == 0)
                throw new InvalidOperationException("Não há páginas livres suficientes.");
            FreePageInterval interval = intervals.Min;
            int amount = (int)Math.Min((ulong)(pageCount - pageIds.Count), interval.Length);
            pageIds.AddRange(Enumerable.Range(0, amount).Select(index => interval.FirstPageId + (ulong)index));
            intervals.Remove(interval);
            if (interval.Length > (ulong)amount)
                intervals.Add(new FreePageInterval(interval.FirstPageId + (ulong)amount, interval.Length - (ulong)amount));
        }
        return pageIds;
    }
}