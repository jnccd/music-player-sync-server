using System.ComponentModel.DataAnnotations;
using EzAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using MusicPlayerSyncServer.Services.Auth;

namespace MusicPlayerSyncServer;

public static class MusicPlayerSyncEndpoints
{
    public static void RegisterNotesEndpoints(this IEndpointRouteBuilder routes, IServiceProvider services)
    {
        routes.MapGet("/keycloak", (
           IOptions<AuthOptions> authOptions) =>
        {
            return Results.Ok(new
            {
                authOptions.Value.KeycloakRealmUrl,
                authOptions.Value.KeycloakClient
            });
        });


    }
}
