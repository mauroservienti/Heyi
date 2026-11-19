namespace Heyi;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NServiceBus;
using NServiceBus.Extensibility;

/// <summary>
/// Turns the <c>Idempotency-Key</c> request header into the identity of the outgoing messages:
/// the n-th message sent while handling a request gets the <c>{key}/{n}</c> message ID, so a retried
/// request produces messages with the same IDs, and receivers can deduplicate them.
/// </summary>
sealed class IdempotencyKey
{
    public const string HeaderName = "Idempotency-Key";

    readonly string value;
    int lastIndex = -1;

    IdempotencyKey(string value) => this.value = value;

    /// <summary>
    /// Reads the key from the request. Returns <c>false</c> when the header is present but invalid:
    /// empty, or sent more than once. A missing header is valid, and yields no key.
    /// </summary>
    public static bool TryRead(HttpRequest request, out IdempotencyKey? key)
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

        key = new IdempotencyKey(raw);
        return true;
    }

    public static ProblemHttpResult Invalid() => TypedResults.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "The Idempotency-Key header is invalid.",
        detail: "Send a single, non-empty Idempotency-Key header, or none at all.");

    /// <summary>
    /// Assigns the next message ID, unless the message already has one set explicitly.
    /// The position counts every message, so explicit IDs don't shift the others.
    /// </summary>
    public void Stamp(ExtendableOptions options)
    {
        var index = Interlocked.Increment(ref lastIndex);

        if (options.GetMessageId() is null)
        {
            options.SetMessageId($"{value}/{index}");
        }
    }
}
