using System;
using System.Collections.Generic;
using System.Text;

namespace AdaptiveStorageEngine;

public enum OperationType
{
    put,
    get,
    delete,
    scan
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

public record SimpleResponse(ulong id, ResponseType response); // Usado nas respostas de Put e Remove
public record GetResponse(ulong id, ResponseType response, string value);
// TODO: Scan Request