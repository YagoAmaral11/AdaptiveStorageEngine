using System;
using System.Collections.Generic;
using System.Text;

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
    not_found
}

public record Request(ulong id, OperationType op);
public record PutRequest(ulong id, OperationType op, ulong key, string value) : Request(id, op);
public record GetRequest(ulong id, OperationType op, ulong key) : Request(id, op);
public record RemoveRequest(ulong id, OperationType op, ulong key) : Request(id, op);
public record ScanRequest(ulong id, OperationType op, ulong start, ulong end) : Request(id, op);
public record ErroredRequest(ulong id, OperationType op, string error, string rawJson) : Request(id, op);

public record SimpleResponse(ulong id, ResponseType response); // Usado nas respostas de Put e Remove
public record GetResponse(ulong id, ResponseType response, string value);
// TODO: Scan Request


public struct Page
{
    // OBS: A SEQUENCIA DOS MEMBROS DESSE STRUCT É A MESMA DOS MEMBROS DO ARQUIVO .DB

    // Header
    public ulong nextPageID;

    // Content
    public string content;
}

public class Metadata
{
    // OBS: A SEQUENCIA DOS MEMBROS DESSA CLASSE É A MESMA DOS MEMBROS DO ARQUIVO .DB

    // Header    
    public ulong pageTablePageId; // o ponteiro para a primeira página da pageTable
    public uint pageTableSize; // o tamanho da pageTable, que é a quantidade do par (64 bits da chave + 64 bits do id da página)
    public ulong tombstoneTablePageId; // o ponteiro para a primeira página da tombstoneTable
    public uint tombstoneTableSize; // o tamanho da pageTable, que é a quantidade do par (64 bits da chave + 32 bits da quantia de páginas seguintes que também estão vazias)

    
    public Dictionary<ulong, ulong> pageTable = []; // A tabela de páginas; O ID do elemento guardado aponta para o ID da primeira página desse elemento    
    public List<ulong> tombstoneTable = []; // A tabela das páginas marcadas como "lixo", páginas que não são mais usadas e que podem ser sobre-escritas.                                                        
}