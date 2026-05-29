using EzAuth.Interfaces;
using EzAuth.Keycloak;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MusicPlayerSyncInterface.DTOs;
using MusicPlayerSyncServer.Database;
using static MusicPlayerSyncServer.Configuration;

namespace MusicPlayerSyncServer.Services.Auth;

[RegisterImplementation(ServiceRegisterType.Scoped, typeof(AuthService))]
public class AuthService(IOptions<AuthOptions> options, LoggerService logger, SongDbContext songDbContext, IEzAuth authBackendService)
{
    readonly bool writeLogs = options.Value.WriteLogs;
    readonly bool give404 = options.Value.Give404;

    /// <summary>
    /// Returns the user associated with the given auth token, or an appropriate error result if the token is invalid or if there was an error during validation.
    /// If the token is valid but the user is not found in the database, a new user will be created based on the information from the token. If give404 is true, a 404 Not Found result will be returned instead of an AuthReqResult when the user is not found.
    /// </summary>
    /// <param name="authTokenHeader">The authorization token header.</param>
    /// <param name="httpClient">The HTTP client for making requests.</param>
    /// <param name="handleRequest">The function to handle the user request.</param>
    /// <returns>The result of the user request.</returns>
    /// <exception cref="NullReferenceException">If parts of the user information from the auth backend is null.</exception>
    public IResult GetUser(string? authTokenHeader, HttpClient httpClient, Func<User, IResult> handleRequest)
    {
        Result<User> userResult = GetUser(authTokenHeader, httpClient);
        if (userResult.IsSuccess)
            return handleRequest(userResult.Value!);
        else
            return userResult.HttpResult ?? Results.Problem("Unknown error");
    }

    /// <summary>
    /// Returns the user associated with the given auth token, or an appropriate error result if the token is invalid or if there was an error during validation.
    /// If the token is valid but the user is not found in the database, a new user will be created based on the information from the token. If give404 is true, a 404 Not Found result will be returned instead of an AuthReqResult when the user is not found.
    /// </summary>
    /// <param name="authTokenHeader">The authorization token header.</param>
    /// <param name="httpClient">The HTTP client for making requests.</param>
    /// <returns>The user associated with the token, or an error result.</returns>
    /// <exception cref="NullReferenceException">If parts of the user information from the auth backend is null.</exception>
    public Result<User> GetUser(string? authTokenHeader, HttpClient httpClient)
    {
        if (authTokenHeader?.Length < 2)
        {
            if (writeLogs)
                logger.WriteLine($"[Auth] Invalid token: {authTokenHeader}");
            return new Result<User>(Results.BadRequest($"Invalid token {authTokenHeader}"));
        }
        EzAuthUserInfo? userInfo;
        try
        {
            if (!authBackendService.IsTokenValid(httpClient, options.Value.AuthBackendRealmUrl ?? "", authTokenHeader?.Split(" ")[1] ?? "", out userInfo))
            {
                if (writeLogs)
                    logger.WriteLine($"[Auth] Invalid token: {authTokenHeader}");
                return new Result<User>(Results.Unauthorized());
            }
        }
        catch (Exception ex)
        {
            if (writeLogs)
                logger.WriteLine($"[Auth] Token check for {authTokenHeader} failed: {ex}");
            return new Result<User>(Results.BadRequest($"Token check failed: {ex.Message}"));
        }

        var apiUser = songDbContext.Users?.FirstOrDefault(u => userInfo != null && u.UserId == userInfo.UserId);
        if (apiUser == null && userInfo?.UserId != null)
        {
            songDbContext.Users?.Add(apiUser = new(
                    userInfo?.UserId ?? throw new NullReferenceException(nameof(userInfo.UserId)),
                    userInfo?.UserHandle ?? throw new NullReferenceException(nameof(userInfo.UserHandle)),
                    userInfo?.UserDisplayName ?? throw new NullReferenceException(nameof(userInfo.UserDisplayName))));
            songDbContext.SaveChanges();
        }
        if (apiUser == null) return new Result<User>(give404 ? Results.NotFound() : new AuthReqResult());
        return new Result<User>(apiUser);
    }
}