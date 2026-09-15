// Copyright (c) Microsoft Corporation.

using System.Security.Claims;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Validation;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace ZavaFinance.Channel;

public sealed class MessagesFunction
{
    private readonly ChannelServiceAdapterBase _adapter;
    private readonly OrchestratorChannel _channel;

    public MessagesFunction(IChannelAdapter adapter, OrchestratorChannel channel)
    {
        _adapter = adapter as ChannelServiceAdapterBase
            ?? throw new InvalidOperationException(
                "The configured channel adapter cannot process Activity Protocol requests.");
        _channel = channel;
    }

    [Function("Messages")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "api/messages")]
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        AuthenticateResult authentication =
            await request.HttpContext.AuthenticateAsync();

        if (!authentication.Succeeded || authentication.Principal is null)
        {
            return new UnauthorizedResult();
        }

        request.HttpContext.User = authentication.Principal;

        IActivity? activity = await HttpHelper.ReadRequestAsync<IActivity>(request);
        if (activity is null
            || !activity.Validate(ValidationContext.Channel | ValidationContext.Receiver))
        {
            return new BadRequestResult();
        }

        ClaimsIdentity? identity = authentication.Principal.Identity as ClaimsIdentity;
        if (identity is null || !identity.IsAuthenticated)
        {
            return new UnauthorizedResult();
        }

        if (!ServiceUrlMatchesClaim(identity, activity.ServiceUrl))
        {
            return new BadRequestObjectResult("Activity service URL does not match its token.");
        }

        InvokeResponse? response = await _adapter.ProcessActivityAsync(
            identity, activity, _channel.OnTurnAsync, cancellationToken);

        await HttpHelper.WriteResponseAsync(request.HttpContext.Response, response);
        return new EmptyResult();
    }

    private static bool ServiceUrlMatchesClaim(ClaimsIdentity identity, string? serviceUrl)
    {
        Claim? serviceUrlClaim = identity.FindFirst("serviceurl");
        if (serviceUrlClaim is null || string.IsNullOrWhiteSpace(serviceUrl))
        {
            return true;
        }

        return Uri.TryCreate(serviceUrlClaim.Value, UriKind.Absolute, out Uri? claimUri)
            && Uri.TryCreate(serviceUrl, UriKind.Absolute, out Uri? activityUri)
            && string.Equals(claimUri.Host, activityUri.Host, StringComparison.OrdinalIgnoreCase);
    }
}
