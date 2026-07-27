using System.Net.Mime;
using Hl7.Fhir.Model;
using UnifyEmpi.Fhir.R4;

namespace UnifyEmpi.Api;

public sealed class FhirResult(
    Resource resource,
    int statusCode = StatusCodes.Status200OK,
    string? location = null,
    string? etag = null) : IResult
{
    public async System.Threading.Tasks.Task ExecuteAsync(HttpContext httpContext)
    {
        var codec = httpContext.RequestServices.GetRequiredService<FhirResourceCodec>();
        var format = NegotiateResponseFormat(httpContext.Request);
        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = FhirResourceCodec.GetContentType(format);
        if (location is not null)
        {
            httpContext.Response.Headers.Location = location;
        }

        if (etag is not null)
        {
            httpContext.Response.Headers.ETag = etag;
        }

        await httpContext.Response.WriteAsync(
            codec.Serialise(resource, format),
            httpContext.RequestAborted);
    }

    public static FhirWireFormat NegotiateResponseFormat(HttpRequest request)
    {
        var requestedFormat = request.Query["_format"].FirstOrDefault();
        if (requestedFormat is not null)
        {
            return FhirResourceCodec.ParseContentType(requestedFormat);
        }

        var accept = request.GetTypedHeaders().Accept;
        if (accept?.Any(static media =>
                media.MediaType.Value?.Contains("xml", StringComparison.OrdinalIgnoreCase) == true)
            == true)
        {
            return FhirWireFormat.Xml;
        }

        return FhirWireFormat.Json;
    }
}

public static class FhirRequest
{
    public const int MaximumBodyBytes = 2 * 1024 * 1024;

    public static async ValueTask<T> ReadAsync<T>(
        HttpRequest request,
        FhirResourceCodec codec,
        CancellationToken cancellationToken)
        where T : Resource
    {
        if (request.ContentLength > MaximumBodyBytes)
        {
            throw new BadHttpRequestException(
                $"FHIR request bodies are limited to {MaximumBodyBytes} bytes.",
                StatusCodes.Status413PayloadTooLarge);
        }

        using var reader = new StreamReader(
            request.Body,
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        var payload = await reader.ReadToEndAsync(cancellationToken);
        if (payload.Length == 0)
        {
            throw new FormatException("The FHIR request body is empty.");
        }

        return codec.Parse<T>(
            payload,
            FhirResourceCodec.ParseContentType(request.ContentType));
    }

    public static long? ParseWeakEtag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalised = value.Trim();
        if (normalised.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            normalised = normalised[2..];
        }

        normalised = normalised.Trim('"');
        return long.TryParse(
            normalised,
            System.Globalization.CultureInfo.InvariantCulture,
            out var version)
            ? version
            : throw new FormatException("If-Match must contain a numeric FHIR version ETag.");
    }

    public static string Etag(long version) =>
        $"W/\"{version.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"";
}

public sealed class FhirExceptionMiddleware(RequestDelegate next, ILogger<FhirExceptionMiddleware> logger)
{
    private static readonly Action<ILogger, string, string, Exception?> LogServerFailure =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(1001, nameof(LogServerFailure)),
            "FHIR request failed with {ExceptionType}; trace {TraceId}");

    private static readonly Action<ILogger, string, string, Exception?> LogRejection =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1002, nameof(LogRejection)),
            "FHIR request rejected with {ExceptionType}; trace {TraceId}");

    public async System.Threading.Tasks.Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            var (status, code, diagnostic) = Map(exception);
            if (status >= 500)
            {
                LogServerFailure(
                    logger,
                    exception.GetType().Name,
                    context.TraceIdentifier,
                    null);
            }
            else
            {
                LogRejection(
                    logger,
                    exception.GetType().Name,
                    context.TraceIdentifier,
                    null);
            }

            var outcome = FhirR4Mapper.CreateOperationOutcome(code, diagnostic);
            await new FhirResult(outcome, status).ExecuteAsync(context);
        }
    }

    private static (int Status, OperationOutcome.IssueType Code, string Diagnostic) Map(
        Exception exception) =>
        exception switch
        {
            UnifyEmpi.Domain.RegistryAuthorisationException =>
                (StatusCodes.Status403Forbidden, OperationOutcome.IssueType.Forbidden,
                    "The authenticated principal is not permitted to perform this operation."),
            UnifyEmpi.Domain.RegistryNotFoundException =>
                (StatusCodes.Status404NotFound, OperationOutcome.IssueType.NotFound,
                    "The requested resource was not found."),
            UnifyEmpi.Domain.RegistryConcurrencyException =>
                (StatusCodes.Status412PreconditionFailed, OperationOutcome.IssueType.Conflict,
                    "The resource changed since the supplied version."),
            UnifyEmpi.Domain.IdempotencyConflictException =>
                (StatusCodes.Status409Conflict, OperationOutcome.IssueType.Conflict,
                    "The idempotency key was already used with different content."),
            UnifyEmpi.Domain.CandidateLimitExceededException =>
                (StatusCodes.Status400BadRequest, OperationOutcome.IssueType.TooCostly,
                    "More identifying data is required because the candidate set exceeded 500 records."),
            UnauthorizedAccessException =>
                (StatusCodes.Status403Forbidden, OperationOutcome.IssueType.Forbidden,
                    "The requested operation is not permitted."),
            NotSupportedException =>
                (StatusCodes.Status415UnsupportedMediaType, OperationOutcome.IssueType.NotSupported,
                    exception.Message),
            BadHttpRequestException badRequest =>
                (badRequest.StatusCode, OperationOutcome.IssueType.Invalid, badRequest.Message),
            FormatException or ArgumentException =>
                (StatusCodes.Status400BadRequest, OperationOutcome.IssueType.Invalid,
                    exception.Message),
            _ =>
                (StatusCodes.Status500InternalServerError, OperationOutcome.IssueType.Exception,
                    "The server could not complete the request. Use the response trace identifier when contacting support.")
        };
}
