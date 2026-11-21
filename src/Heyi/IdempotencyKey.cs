namespace Heyi;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NServiceBus;
using NServiceBus.Extensibility;

/// <summary>
/// Turns the <c>Idempotency-Key</c> request header into the identity of the outgoing messages:
/// the n-th message of a given type sent while handling a request gets the <c>{key}/{type}/{n}</c> message ID,
/// where <c>{type}</c> is a short hash of the message type's full name. Sends with an explicit destination get
/// the <c>{key}/{type}/{destination}/{n}</c> message ID instead, counted per destination. A retried request
/// produces messages with the same IDs, and receivers can deduplicate them. Messages sent through
/// <see cref="IOutgoingMessages{TSender}"/> are counted per sender too, and get the <c>{key}/{sender}/{type}/{n}</c> message ID.
/// With a scope configured, for example the client's identity, a hash of it follows the key: <c>{key}/{scope}/...</c>.
/// </summary>
/// <remarks>
/// Counting per type and destination keeps the IDs deterministic when different message types, or the same type
/// to different destinations, are sent concurrently, for example by ServiceComposer's composition handlers.
/// </remarks>
sealed class IdempotencyKey
{
    public const string HeaderName = "Idempotency-Key";

    static readonly ConcurrentDictionary<Type, string> typeTags = new();

    readonly string value;
    readonly ConcurrentDictionary<(Type? Sender, Type MessageType, string? Destination), int> lastIndexes = new();

    IdempotencyKey(string value) => this.value = value;

    /// <summary>
    /// Reads the key from the request. Returns <c>false</c> when the header is present but invalid:
    /// empty, or sent more than once. A missing header is valid, and yields no key.
    /// </summary>
    public static bool TryRead(HttpRequest request, out IdempotencyKey? key) => TryRead(request, scope: null, out key);

    /// <summary>
    /// Reads the key from the request, as <see cref="TryRead(HttpRequest, out IdempotencyKey?)"/> does, and scopes it
    /// with <paramref name="scope"/>: a non-empty scope is hashed and becomes part of every message ID, as <c>{key}/{scope}/...</c>.
    /// The scope is only evaluated when the request carries a key.
    /// </summary>
    public static bool TryRead(HttpRequest request, Func<HttpContext, string?>? scope, out IdempotencyKey? key)
    {
        key = null;

        if (!request.Headers.TryGetValue(HeaderName, out var values))
        {
            return true;
        }

        if (values.Count != 1)
        {
            return false;
        }

        // The IETF draft defines the key as a structured field string, which is quoted.
        // Unquoted keys are accepted too.
        var raw = values[0]?.Trim() ?? string.Empty;
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            raw = raw[1..^1];
        }

        if (raw.Length == 0)
        {
            return false;
        }

        var scopeValue = scope?.Invoke(request.HttpContext);
        key = new IdempotencyKey(string.IsNullOrEmpty(scopeValue) ? raw : $"{raw}/{Tag(scopeValue)}");
        return true;
    }

    public static ProblemHttpResult Invalid() => TypedResults.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "The Idempotency-Key header is invalid.",
        detail: "Send a single, non-empty Idempotency-Key header, or none at all.");

    /// <summary>
    /// Assigns the next message ID for the sender, if any, the message type, and the explicit destination, if any,
    /// unless the message already has one set explicitly. The position counts every such message,
    /// so explicit IDs don't shift the others.
    /// </summary>
    public void Stamp(ExtendableOptions options, Type messageType, Type? sender = null)
    {
        var destination = options is SendOptions sendOptions ? sendOptions.GetDestination() : null;
        var index = lastIndexes.AddOrUpdate((sender, messageType, destination), 0, (_, lastIndex) => lastIndex + 1);

        if (options.GetMessageId() is null)
        {
            var senderSegment = sender is null ? string.Empty : $"/{TypeTag(sender)}";
            var destinationSegment = destination is null ? string.Empty : $"/{Tag(destination)}";
            options.SetMessageId($"{value}{senderSegment}/{TypeTag(messageType)}{destinationSegment}/{index}");
        }
    }

    /// <summary>
    /// The first 8 hexadecimal characters of the SHA-256 hash of the type's full name: stable across
    /// processes and machines, and short enough to keep message IDs within transport limits.
    /// </summary>
    internal static string TypeTag(Type messageType) =>
        typeTags.GetOrAdd(messageType, type => Tag(type.FullName ?? type.Name));

    /// <summary>
    /// The first 8 hexadecimal characters of the SHA-256 hash of <paramref name="value"/>.
    /// </summary>
    internal static string Tag(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
}
