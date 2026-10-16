using Azure;
using BotMeetings.TranscriptDiagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BotMeetings.LiveContext;

public static class LiveContextEndpoints
{
    public static void MapLiveContextEndpoints(this WebApplication app, string tenantId)
    {
        app.MapGet("/api/acs/token", async (
            HttpContext httpContext,
            IAcsTokenService tokenService,
            IOptions<LiveContextOptions> configuredOptions,
            ILogger<AcsTokenService> logger,
            CancellationToken cancellationToken) =>
        {
            var unavailable = CheckAvailabilityAndAuthorization(httpContext, configuredOptions.Value, true, logger);
            if (unavailable is not null) return unavailable;

            try
            {
                return Results.Ok(await tokenService.CreateAsync(cancellationToken));
            }
            catch (RequestFailedException exception)
            {
                logger.LogError(exception, "ACS identity token creation failed with status {Status}.", exception.Status);
                return Results.Problem(
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "ACS token creation failed",
                    detail: "The communication identity service could not issue a token.");
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                logger.LogError("The ACS connection configuration is invalid.");
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Live context is misconfigured",
                    detail: "The communication service configuration is invalid.");
            }
        })
        .WithName("CreateAcsToken")
        .WithSummary("Create a short-lived ACS browser token")
        .Produces<AcsTokenResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/live-context/meetings/resolve", (
            ResolveActiveMeetingRequest request,
            HttpContext httpContext,
            ActiveMeetingStore activeMeetings,
            IOptions<LiveContextOptions> configuredOptions,
            ILogger<ActiveMeetingStore> logger) =>
        {
            var unavailable = CheckAvailabilityAndAuthorization(
                httpContext,
                configuredOptions.Value,
                false,
                logger);
            if (unavailable is not null) return unavailable;

            if (string.IsNullOrWhiteSpace(request.MeetingLink) ||
                request.MeetingLink.Trim().Length > 2048 ||
                !Uri.TryCreate(request.MeetingLink.Trim(), UriKind.Absolute, out var meetingUri) ||
                !string.Equals(meetingUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(request.MeetingLink)] = ["A valid HTTPS meeting link is required."]
                });
            }

            if (!activeMeetings.TryResolve(tenantId, request.MeetingLink, out var target) ||
                string.IsNullOrWhiteSpace(target?.MeetingId))
            {
                logger.LogInformation("No active meeting matched the supplied join link for the configured tenant.");
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Active meeting not found",
                    detail: "No active meeting matched the supplied link.");
            }

            return Results.Ok(new ActiveMeetingResponse(target.MeetingId));
        })
        .WithMetadata(new RequestSizeLimitAttribute(8 * 1024))
        .WithName("ResolveActiveMeeting")
        .WithSummary("Resolve a Teams join link to the active internal meeting identifier")
        .Produces<ActiveMeetingResponse>()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/live-context/captions", async (
            CreateLiveCaptionRequest request,
            HttpContext httpContext,
            ActiveMeetingStore activeMeetings,
            ILiveContextStore store,
            IOptions<LiveContextOptions> configuredOptions,
            ILogger<FileLiveContextStore> logger,
            CancellationToken cancellationToken) =>
        {
            var options = configuredOptions.Value;
            var unavailable = CheckAvailabilityAndAuthorization(httpContext, options, false, logger);
            if (unavailable is not null) return unavailable;

            var validationProblem = Validate(request, options);
            if (validationProblem is not null) return validationProblem;

            if (!activeMeetings.IsActive(tenantId, request.MeetingId.Trim()))
            {
                logger.LogInformation("A caption was rejected because it did not match an active meeting.");
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Active meeting not found",
                    detail: "The supplied meeting is not currently active.");
            }

            var receivedAt = DateTimeOffset.UtcNow;
            try
            {
                var result = await store.AddAsync(tenantId, request, receivedAt, cancellationToken);
                logger.LogInformation(
                    "Live caption {CaptionId} for meeting {MeetingId} was {Disposition}; received {ReceivedAt}, persisted {PersistedAt}.",
                    result.Caption.Id,
                    request.MeetingId,
                    result.Duplicate ? "deduplicated" : "persisted",
                    result.Caption.ReceivedAt,
                    result.Caption.PersistedAt);
                return Results.Ok(new LiveCaptionReceiptResponse(
                    true,
                    result.Duplicate,
                    result.Caption.Id,
                    result.Caption.ReceivedAt,
                    result.Caption.PersistedAt));
            }
            catch (LiveContextCapacityException)
            {
                logger.LogWarning("Live caption capacity was reached for meeting {MeetingId}.", request.MeetingId);
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Live context capacity reached",
                    detail: "This meeting cannot accept additional captions.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogError(exception, "Live caption persistence failed for meeting {MeetingId}.", request.MeetingId);
                return Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Live caption persistence failed",
                    detail: "The caption could not be persisted.");
            }
        })
        .WithMetadata(new RequestSizeLimitAttribute(32 * 1024))
        .WithName("CreateLiveCaption")
        .WithSummary("Persist a final live caption")
        .Produces<LiveCaptionReceiptResponse>()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static IResult? CheckAvailabilityAndAuthorization(
        HttpContext context,
        LiveContextOptions options,
        bool requireAcs,
        ILogger logger)
    {
        if (!options.Enabled)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Live context is disabled",
                detail: "The live-context POC is not enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.AccessKey) ||
            string.IsNullOrWhiteSpace(options.DataPath) ||
            options.MaximumCaptionsPerMeeting is < 1 or > 10000 ||
            options.MaximumCaptionAgeHours is < 1 or > 168 ||
            (requireAcs && string.IsNullOrWhiteSpace(options.AcsConnectionString)))
        {
            logger.LogError("The live-context endpoint is enabled but required configuration is missing.");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Live context is misconfigured",
                detail: "Required server configuration is missing.");
        }

        var suppliedKey = context.Request.Headers["X-Live-Context-Key"].FirstOrDefault();
        if (!LiveContextKeyValidator.IsValid(suppliedKey, options.AccessKey))
        {
            logger.LogWarning("A live-context request was rejected because authentication failed.");
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: "A valid live-context key is required.");
        }

        return null;
    }

    private static IResult? Validate(CreateLiveCaptionRequest request, LiveContextOptions options)
    {
        var errors = new Dictionary<string, string[]>();
        AddRequiredAndLengthError(errors, nameof(request.MeetingId), request.MeetingId, 256);
        AddRequiredAndLengthError(errors, nameof(request.SpeakerId), request.SpeakerId, 256);
        AddRequiredAndLengthError(errors, nameof(request.SpeakerDisplayName), request.SpeakerDisplayName, 200);
        AddRequiredAndLengthError(errors, nameof(request.SpokenText), request.SpokenText, 4000);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        if (!string.Equals(request.ResultType?.Trim(), "Final", StringComparison.Ordinal))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Unsupported caption result type",
                detail: "Only final caption results are accepted.");
        }

        var now = DateTimeOffset.UtcNow;
        if (request.Timestamp == default ||
            request.Timestamp > now.AddMinutes(5) ||
            request.Timestamp < now.AddHours(-options.MaximumCaptionAgeHours))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Timestamp)] = ["Timestamp must be a valid recent time and no more than five minutes in the future."]
            });
        }

        return null;
    }

    private static void AddRequiredAndLengthError(
        IDictionary<string, string[]> errors,
        string field,
        string? value,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[field] = ["The value is required."];
        }
        else if (value.Trim().Length > maximumLength)
        {
            errors[field] = [$"The value must not exceed {maximumLength} characters."];
        }
    }
}
