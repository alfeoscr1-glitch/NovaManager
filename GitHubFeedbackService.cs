using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NovaManager;

internal static class GitHubFeedbackService
{
    private const string ClientId = "Ov23liOEfdedlMkjGw0z";
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string AccessTokenUrl = "https://github.com/login/oauth/access_token";
    private const string IssuesUrl = "https://api.github.com/repos/alfeoscr1-glitch/NovaManager/issues";
    private const string TokenFilePathName = "github-feedback-authorization.dat";
    private const string LegacyTokenFilePathName = "github-feedback-token.dat";
    private static readonly SemaphoreSlim AuthorizationLock = new(1, 1);
    private static readonly HttpClient Client = CreateHttpClient();
    private static string TokenFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NovaSoftwareManager",
        TokenFilePathName);
    private static string LegacyTokenFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NovaSoftwareManager",
        LegacyTokenFilePathName);

    public static async Task<string> SubmitIssueAsync(
        string title,
        string body,
        string label,
        Func<string, string, Task> authorizeUser,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        if (label is not ("enhancement" or "bug"))
        {
            throw new ArgumentOutOfRangeException(nameof(label), "The GitHub issue label is not supported.");
        }

        var authorization = await GetAuthorizationAsync(authorizeUser, cancellationToken);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, IssuesUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorization.AccessToken);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { title, body, labels = new[] { label } }),
                Encoding.UTF8,
                "application/json");

            using var response = await Client.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                if (!string.IsNullOrWhiteSpace(authorization.RefreshToken))
                {
                    try
                    {
                        authorization = await RefreshAuthorizationAsync(authorization.RefreshToken, cancellationToken);
                    }
                    catch (GitHubFeedbackException exception) when (
                        exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                    {
                        DeleteStoredAuthorization();
                        authorization = await AuthorizeAsync(authorizeUser, cancellationToken);
                    }
                }
                else
                {
                    DeleteStoredAuthorization();
                    authorization = await AuthorizeAsync(authorizeUser, cancellationToken);
                }

                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw CreateSubmissionException(response.StatusCode, responseBody);
            }

            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("html_url", out var urlElement) ||
                !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var issueUri) ||
                issueUri.Scheme != Uri.UriSchemeHttps ||
                !issueUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                !issueUri.AbsolutePath.StartsWith(
                    "/alfeoscr1-glitch/NovaManager/issues/",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("GitHub accepted the report but did not return a valid issue link.");
            }

            return issueUri.AbsoluteUri;
        }

        throw new InvalidOperationException("GitHub authorization did not succeed. Please try sending the report again.");
    }

    private static async Task<GitHubAuthorization> GetAuthorizationAsync(
        Func<string, string, Task> authorizeUser,
        CancellationToken cancellationToken)
    {
        await AuthorizationLock.WaitAsync(cancellationToken);
        try
        {
            var savedAuthorization = await ReadAuthorizationAsync(cancellationToken);
            if (savedAuthorization is not null)
            {
                if (savedAuthorization.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
                {
                    return savedAuthorization;
                }

                if (!string.IsNullOrWhiteSpace(savedAuthorization.RefreshToken))
                {
                    try
                    {
                        return await RefreshAuthorizationAsync(savedAuthorization.RefreshToken, cancellationToken);
                    }
                    catch (GitHubFeedbackException exception) when (
                        exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                    {
                        DeleteStoredAuthorization();
                    }
                }
                else if (savedAuthorization.ExpiresAtUtc == DateTimeOffset.MaxValue)
                {
                    return savedAuthorization;
                }
            }

            return await AuthorizeAsync(authorizeUser, cancellationToken);
        }
        finally
        {
            AuthorizationLock.Release();
        }
    }

    private static async Task<GitHubAuthorization> AuthorizeAsync(
        Func<string, string, Task> authorizeUser,
        CancellationToken cancellationToken)
    {
        var device = await RequestDeviceCodeAsync(cancellationToken);
        await authorizeUser(device.UserCode, device.VerificationUri);

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresIn);
        var interval = TimeSpan.FromSeconds(Math.Max(device.Interval, 5));
        while (DateTimeOffset.UtcNow < expiresAt)
        {
            await Task.Delay(interval, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, AccessTokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["device_code"] = device.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                })
            };
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await Client.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw CreateAuthorizationException(response.StatusCode, responseBody);
            }

            var tokenResult = JsonSerializer.Deserialize<AccessTokenResponse>(responseBody)
                ?? throw new InvalidDataException("GitHub returned an empty authorization response.");
            if (!string.IsNullOrWhiteSpace(tokenResult.AccessToken))
            {
                var authorization = CreateAuthorization(tokenResult);
                await StoreAuthorizationAsync(authorization, cancellationToken);
                return authorization;
            }

            switch (tokenResult.Error)
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    break;
                case "access_denied":
                    throw new InvalidOperationException("GitHub authorization was denied; no report was submitted.");
                case "expired_token":
                    throw new InvalidOperationException("The GitHub sign-in code expired; try sending the report again.");
                default:
                    throw new InvalidOperationException(
                        $"GitHub authorization could not complete: {tokenResult.ErrorDescription ?? tokenResult.Error ?? "unknown response"}");
            }
        }

        throw new InvalidOperationException("The GitHub sign-in code expired; try sending the report again.");
    }

    private static async Task<DeviceCodeResponse> RequestDeviceCodeAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scope"] = "public_repo"
            })
        };
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await Client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateAuthorizationException(response.StatusCode, responseBody);
        }

        var device = JsonSerializer.Deserialize<DeviceCodeResponse>(responseBody)
            ?? throw new InvalidDataException("GitHub returned an empty sign-in response.");
        if (string.IsNullOrWhiteSpace(device.DeviceCode) ||
            string.IsNullOrWhiteSpace(device.UserCode) ||
            !Uri.TryCreate(device.VerificationUri, UriKind.Absolute, out var verificationUri) ||
            verificationUri.Scheme != Uri.UriSchemeHttps ||
            !verificationUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub returned invalid device sign-in details.");
        }

        return device;
    }

    private static async Task<GitHubAuthorization> RefreshAuthorizationAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AccessTokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            })
        };
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await Client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var tokenResult = JsonSerializer.Deserialize<AccessTokenResponse>(responseBody);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateAuthorizationException(response.StatusCode, responseBody);
        }

        if (tokenResult is null || !string.IsNullOrWhiteSpace(tokenResult.Error))
        {
            throw new GitHubFeedbackException(
                $"GitHub could not refresh the saved authorization. {tokenResult?.ErrorDescription ?? tokenResult?.Error ?? "Try again."}",
                HttpStatusCode.Unauthorized);
        }

        var authorization = CreateAuthorization(tokenResult);
        if (string.IsNullOrWhiteSpace(authorization.AccessToken))
        {
            throw new InvalidDataException("GitHub returned no access token while refreshing the saved authorization.");
        }

        await StoreAuthorizationAsync(authorization, cancellationToken);
        return authorization;
    }

    private static GitHubAuthorization CreateAuthorization(AccessTokenResponse tokenResult)
    {
        if (string.IsNullOrWhiteSpace(tokenResult.AccessToken))
        {
            throw new InvalidDataException("GitHub did not return an access token.");
        }

        var expiresAtUtc = tokenResult.ExpiresIn is > 0
            ? DateTimeOffset.UtcNow.AddSeconds(tokenResult.ExpiresIn.Value)
            : DateTimeOffset.MaxValue;
        return new GitHubAuthorization(
            tokenResult.AccessToken,
            tokenResult.RefreshToken,
            expiresAtUtc);
    }

    private static async Task<GitHubAuthorization?> ReadAuthorizationAsync(CancellationToken cancellationToken)
    {
        var authorizationPath = File.Exists(TokenFilePath)
            ? TokenFilePath
            : LegacyTokenFilePath;
        if (!File.Exists(authorizationPath))
        {
            return null;
        }

        byte[] protectedAuthorization;
        try
        {
            protectedAuthorization = await File.ReadAllBytesAsync(authorizationPath, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        var authorizationBytes = Unprotect(protectedAuthorization);
        try
        {
            try
            {
                var authorization = JsonSerializer.Deserialize<GitHubAuthorization>(
                    authorizationBytes,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("Nova's saved GitHub authorization file is empty.");
                if (!string.Equals(authorizationPath, TokenFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    await StoreAuthorizationAsync(authorization, cancellationToken);
                }

                return authorization;
            }
            catch (JsonException)
            {
                // The earlier 1.2.2 build protected only the access token; preserve it and upgrade the stored format.
                var legacyToken = Encoding.UTF8.GetString(authorizationBytes);
                if (string.IsNullOrWhiteSpace(legacyToken))
                {
                    throw new InvalidDataException("Nova's saved GitHub authorization file is invalid.");
                }

                var legacyAuthorization = new GitHubAuthorization(
                    legacyToken,
                    null,
                    DateTimeOffset.MaxValue);
                await StoreAuthorizationAsync(legacyAuthorization, cancellationToken);
                return legacyAuthorization;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(authorizationBytes);
        }
    }

    private static async Task StoreAuthorizationAsync(
        GitHubAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var authorizationBytes = JsonSerializer.SerializeToUtf8Bytes(authorization);
        byte[] protectedToken;
        try
        {
            protectedToken = Protect(authorizationBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(authorizationBytes);
        }

        var directory = Path.GetDirectoryName(TokenFilePath)
            ?? throw new InvalidOperationException("Nova could not locate the local GitHub authorization folder.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{TokenFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, protectedToken, cancellationToken);
            File.Move(temporaryPath, TokenFilePath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedToken);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void DeleteStoredAuthorization()
    {
        if (File.Exists(TokenFilePath))
        {
            File.Delete(TokenFilePath);
        }
    }

    private static byte[] Protect(byte[] data)
    {
        var input = CreateBlob(data);
        DataBlob entropy = default;
        try
        {
            if (!CryptProtectData(ref input, "Nova Manager GitHub feedback", ref entropy,
                    IntPtr.Zero, IntPtr.Zero, 1, out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not protect the GitHub authorization token.");
            }

            try
            {
                var protectedData = new byte[output.Size];
                Marshal.Copy(output.Data, protectedData, 0, output.Size);
                return protectedData;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            FreeBlob(ref input);
        }
    }

    private static byte[] Unprotect(byte[] data)
    {
        var input = CreateBlob(data);
        DataBlob entropy = default;
        try
        {
            if (!CryptUnprotectData(ref input, out var description, ref entropy,
                    IntPtr.Zero, IntPtr.Zero, 1, out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read Nova's saved GitHub authorization.");
            }

            try
            {
                var clearData = new byte[output.Size];
                Marshal.Copy(output.Data, clearData, 0, output.Size);
                return clearData;
            }
            finally
            {
                LocalFree(output.Data);
                if (description != IntPtr.Zero)
                {
                    LocalFree(description);
                }
            }
        }
        finally
        {
            FreeBlob(ref input);
        }
    }

    private static DataBlob CreateBlob(byte[] data)
    {
        var pointer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        return new DataBlob { Size = data.Length, Data = pointer };
    }

    private static void FreeBlob(ref DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
        {
            RtlSecureZeroMemory(blob.Data, (UIntPtr)blob.Size);
            Marshal.FreeHGlobal(blob.Data);
            blob = default;
        }
    }

    private static GitHubFeedbackException CreateSubmissionException(HttpStatusCode statusCode, string responseBody)
    {
        var detail = GetGitHubError(responseBody);
        var message = statusCode switch
        {
            HttpStatusCode.Unauthorized => "GitHub no longer accepts the saved sign-in. Please send the report again to reauthorize.",
            HttpStatusCode.Forbidden => "GitHub denied issue creation. Confirm your account can create issues in the Nova Manager repository.",
            HttpStatusCode.NotFound => "The Nova Manager GitHub repository or its Issues feature could not be found.",
            HttpStatusCode.UnprocessableEntity => $"GitHub rejected the issue details or label. {detail}",
            _ => $"GitHub could not create the issue (HTTP {(int)statusCode}). {detail}"
        };
        return new GitHubFeedbackException(message, statusCode);
    }

    private static GitHubFeedbackException CreateAuthorizationException(HttpStatusCode statusCode, string responseBody)
    {
        var detail = GetGitHubError(responseBody);
        return new GitHubFeedbackException(
            $"GitHub sign-in could not be started (HTTP {(int)statusCode}). {detail}",
            statusCode);
    }

    private static string GetGitHubError(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error_description", out var description) &&
                description.GetString() is { Length: > 0 } errorDescription)
            {
                return errorDescription;
            }

            if (document.RootElement.TryGetProperty("message", out var message) &&
                message.GetString() is { Length: > 0 } errorMessage)
            {
                return errorMessage;
            }
        }
        catch (JsonException)
        {
        }

        return "Check the GitHub account permissions and try again.";
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NovaSoftwareManager", "1.2.2"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private sealed record DeviceCodeResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("device_code")] string DeviceCode,
        [property: System.Text.Json.Serialization.JsonPropertyName("user_code")] string UserCode,
        [property: System.Text.Json.Serialization.JsonPropertyName("verification_uri")] string VerificationUri,
        [property: System.Text.Json.Serialization.JsonPropertyName("expires_in")] int ExpiresIn,
        [property: System.Text.Json.Serialization.JsonPropertyName("interval")] int Interval);

    private sealed record AccessTokenResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string? AccessToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("error")] string? Error,
        [property: System.Text.Json.Serialization.JsonPropertyName("error_description")] string? ErrorDescription,
        [property: System.Text.Json.Serialization.JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: System.Text.Json.Serialization.JsonPropertyName("refresh_token_expires_in")] int? RefreshTokenExpiresIn);

    private sealed record GitHubAuthorization(
        string AccessToken,
        string? RefreshToken,
        DateTimeOffset ExpiresAtUtc);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", EntryPoint = "RtlSecureZeroMemory")]
    private static extern IntPtr RtlSecureZeroMemory(IntPtr memory, UIntPtr length);
}

internal sealed class GitHubFeedbackException(string message, HttpStatusCode statusCode)
    : InvalidOperationException(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
