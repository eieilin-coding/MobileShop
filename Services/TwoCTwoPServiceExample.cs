using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace MobileShop.Services;

public record TwoCTwoPTokenResult(string RespCode, string RespDesc, string? WebPaymentUrl);

public record TwoCTwoPPaymentResult(
    string MerchantId,
    string InvoiceNo,
    decimal Amount,
    string CurrencyCode,
    string RespCode,
    string ReferenceNo);

public class TwoCTwoPService
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUrl;
    private readonly string _apiVersion;
    private readonly string _merchantId;
    private readonly string _currencyCode;
    private readonly SymmetricSecurityKey _signingKey;

    public TwoCTwoPService(HttpClient httpClient, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(configuration);

        _httpClient = httpClient;
        var baseUrl = RequireSetting(configuration, "BaseUrl");
        _apiVersion = RequireSetting(configuration, "ApiVersion");
        _merchantId = RequireSetting(configuration, "MerchantId");
        var secretKey = RequireSetting(configuration, "SecretKey");
        _currencyCode = RequireSetting(configuration, "CurrencyCode");

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !IsTwoCTwoPHost(uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || (uri.AbsolutePath != "/" && uri.AbsolutePath != ""))
        {
            throw new InvalidOperationException("TwoC2P:BaseUrl must be an HTTPS 2c2p.com base URL.");
        }

        if (_apiVersion.Split('.').Any(part => part.Length == 0 || part.Any(c => c < '0' || c > '9')))
        {
            throw new InvalidOperationException("TwoC2P:ApiVersion is invalid.");
        }

        if (_currencyCode.Length != 3 || _currencyCode.Any(c => c < 'A' || c > 'Z'))
        {
            throw new InvalidOperationException("TwoC2P:CurrencyCode must be a three-letter ISO currency code.");
        }

        var secretBytes = Encoding.UTF8.GetBytes(secretKey);
        if (secretBytes.Length < 32)
        {
            throw new InvalidOperationException("TwoC2P:SecretKey must contain at least 256 bits in UTF-8.");
        }

        _baseUrl = uri;
        _signingKey = new SymmetricSecurityKey(secretBytes);
    }

    public async Task<TwoCTwoPTokenResult> CreatePaymentTokenAsync(
        string invoiceNo, decimal amount, string frontendReturnUrl, string? backendReturnUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceNo);
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");
        }

        ValidateReturnUrl(frontendReturnUrl, nameof(frontendReturnUrl));
        if (backendReturnUrl is not null)
        {
            ValidateReturnUrl(backendReturnUrl, nameof(backendReturnUrl));
        }

        var payload = new JwtPayload
        {
            { "merchantID", _merchantId },
            { "invoiceNo", invoiceNo },
            { "description", "Order payment" },
            { "amount", amount },
            { "currencyCode", _currencyCode },
            { "frontendReturnUrl", frontendReturnUrl }
        };
        if (backendReturnUrl is not null)
        {
            payload.Add("backendReturnUrl", backendReturnUrl);
        }

        var jwt = await PostAsync("paymentToken", payload);
        using var document = ReadVerifiedPayload(jwt);
        var root = document.RootElement;
        var respCode = RequiredString(root, "respCode");
        var respDesc = RequiredString(root, "respDesc");
        string? webPaymentUrl = null;

        if (root.TryGetProperty("webPaymentUrl", out var urlElement) && urlElement.ValueKind != JsonValueKind.Null)
        {
            if (urlElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(urlElement.GetString()) ||
                !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var url) ||
                url.Scheme != Uri.UriSchemeHttps || !IsTwoCTwoPHost(url) ||
                !string.IsNullOrEmpty(url.UserInfo))
            {
                throw new InvalidDataException("Invalid 2C2P webPaymentUrl.");
            }

            webPaymentUrl = urlElement.GetString();
        }

        return new TwoCTwoPTokenResult(respCode, respDesc, webPaymentUrl);
    }

    public async Task<TwoCTwoPPaymentResult> InquireAsync(string invoiceNo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceNo);
        var payload = new JwtPayload
        {
            { "merchantID", _merchantId },
            { "invoiceNo", invoiceNo }
        };

        var jwt = await PostAsync("paymentInquiry", payload);
        var result = VerifyBackendPayload(jwt);
        if (!string.Equals(result.InvoiceNo, invoiceNo, StringComparison.Ordinal))
        {
            throw new InvalidDataException("2C2P response invoice number does not match the request.");
        }

        return result;
    }

    public TwoCTwoPPaymentResult VerifyBackendPayload(string jwt)
    {
        using var document = ReadVerifiedPayload(jwt);
        var root = document.RootElement;
        var merchantId = RequiredString(root, "merchantID");
        if (!string.Equals(merchantId, _merchantId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("2C2P response merchant ID does not match the configured merchant.");
        }

        return new TwoCTwoPPaymentResult(
            merchantId,
            RequiredString(root, "invoiceNo"),
            RequiredAmount(root),
            RequiredString(root, "currencyCode"),
            RequiredString(root, "respCode"),
            RequiredString(root, "referenceNo"));
    }

    private async Task<string> PostAsync(string operation, JwtPayload payload)
    {
        var header = new JwtHeader(new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256));
        var jwt = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
        var endpoint = new Uri(_baseUrl, $"/payment/{_apiVersion}/{operation}");

        using var response = await _httpClient.PostAsJsonAsync(endpoint, new { payload = jwt });
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Invalid 2C2P response.");
        }

        return RequiredString(document.RootElement, "payload");
    }

    private JsonDocument ReadVerifiedPayload(string jwt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jwt);
        var parts = jwt.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty))
        {
            throw new SecurityTokenException("Invalid signed 2C2P JWT.");
        }

        var parameters = new TokenValidationParameters
        {
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _signingKey,
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            RequireExpirationTime = false,
            ClockSkew = TimeSpan.Zero
        };
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        handler.ValidateToken(jwt, parameters, out var validatedToken);
        if (validatedToken is not JwtSecurityToken validatedJwt ||
            !string.Equals(validatedJwt.Header.Alg, SecurityAlgorithms.HmacSha256, StringComparison.Ordinal))
        {
            throw new SecurityTokenException("Invalid 2C2P JWT algorithm.");
        }

        var document = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1]));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new InvalidDataException("Invalid 2C2P JWT payload.");
        }

        if (document.RootElement.TryGetProperty("exp", out var expiration) &&
            (expiration.ValueKind != JsonValueKind.Number || !expiration.TryGetDecimal(out var seconds) ||
             seconds <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000m))
        {
            document.Dispose();
            throw new SecurityTokenException("Invalid or expired 2C2P JWT expiration.");
        }

        return document;
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Missing or invalid 2C2P field: {name}.");
        }

        return value.GetString()!;
    }

    private static decimal RequiredAmount(JsonElement root)
    {
        if (!root.TryGetProperty("amount", out var value))
        {
            throw new InvalidDataException("Missing 2C2P amount.");
        }

        decimal amount = 0;
        var valid = value.ValueKind == JsonValueKind.Number
            ? value.TryGetDecimal(out amount)
            : value.ValueKind == JsonValueKind.String &&
              decimal.TryParse(value.GetString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                  CultureInfo.InvariantCulture, out amount);

        if (!valid || amount <= 0)
        {
            throw new InvalidDataException("Invalid 2C2P amount.");
        }

        return amount;
    }

    private static string RequireSetting(IConfiguration configuration, string name)
    {
        var value = configuration[$"TwoC2P:{name}"];
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new InvalidOperationException($"Missing or invalid TwoC2P:{name} setting.");
        }

        return value;
    }

    private static bool IsTwoCTwoPHost(Uri uri) =>
        string.Equals(uri.IdnHost, "2c2p.com", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.EndsWith(".2c2p.com", StringComparison.OrdinalIgnoreCase);

    private static void ValidateReturnUrl(string url, string parameterName)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Return URL must be an absolute HTTPS URL.", parameterName);
        }
    }
}
