using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ManagerPaperworkSystem.WinForms;

internal static class ActivationCodeCodec
{
    private const string RequestPrefix = "HKREQ2-";
    private const string LicensePrefix = "HKLIC2-";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static string FormatRequest(DeviceLicenseRequestV2 request)
        => $"HISAB KITAB ACTIVATION REQUEST\r\n" +
           $"Store GUID: {request.StoreGuid}\r\n" +
           $"Store Name: {request.BusinessName}\r\n" +
           $"State: {request.StoreState}\r\n" +
           $"Business Type: {request.BusinessType}\r\n" +
           $"ZIP Code: {request.StoreZip}\r\n" +
           $"PC ID: {request.DeviceId}\r\n" +
           $"App Version: {request.AppVersion}\r\n\r\n" +
           $"Request Code:\r\n{Encode(RequestPrefix, request)}";

    public static DeviceLicenseRequestV2 DecodeRequest(string text)
        => Decode<DeviceLicenseRequestV2>(RequestPrefix, text, "activation request");

    public static string FormatLicense(DeviceLicensePayloadV2 payload, string licenseJson)
        => $"HISAB KITAB LICENSE ACTIVATION\r\n" +
           $"License Key: {DisplayLicenseKey(payload)}\r\n" +
           $"Store GUID: {payload.StoreGuid}\r\n" +
           $"Store Name: {payload.BusinessName}\r\n" +
           $"State: {payload.StoreState}\r\n" +
           $"Business Type: {payload.BusinessType}\r\n" +
           $"ZIP Code: {payload.StoreZip}\r\n" +
           $"PC ID: {payload.DeviceId}\r\n\r\n" +
           $"Protected License Code:\r\n{EncodeRaw(LicensePrefix, licenseJson)}";

    public static string DisplayLicenseKey(DeviceLicensePayloadV2 payload)
    {
        var source = new string((payload.ActivationId ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (source.Length < 16)
            source = Guid.NewGuid().ToString("N").ToUpperInvariant();
        return $"HKL-{source[..4]}-{source[4..8]}-{source[8..12]}-{source[12..16]}";
    }

    public static string? TryExtractDisplayLicenseKey(string activationText)
    {
        foreach (var line in (activationText ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            const string label = "License Key:";
            if (!line.TrimStart().StartsWith(label, StringComparison.OrdinalIgnoreCase))
                continue;
            var candidate = line[(line.IndexOf(label, StringComparison.OrdinalIgnoreCase) + label.Length)..].Trim();
            if (candidate.StartsWith("HKL-", StringComparison.OrdinalIgnoreCase))
                return candidate.ToUpperInvariant();
        }

        try
        {
            var licenseJson = DecodeLicenseJson(activationText ?? "");
            var envelope = JsonSerializer.Deserialize<DeviceLicenseEnvelopeV2>(licenseJson, JsonOptions);
            var payload = envelope is null
                ? null
                : JsonSerializer.Deserialize<DeviceLicensePayloadV2>(Convert.FromBase64String(envelope.Payload), JsonOptions);
            return payload is null ? null : DisplayLicenseKey(payload);
        }
        catch
        {
            return null;
        }
    }

    public static string DecodeLicenseJson(string text)
        => DecodeRaw(LicensePrefix, text, "license key");

    private static string Encode<T>(string prefix, T value)
        => EncodeRaw(prefix, JsonSerializer.Serialize(value, JsonOptions));

    private static string EncodeRaw(string prefix, string json)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(json));
        return prefix + Convert.ToBase64String(output.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static T Decode<T>(string prefix, string text, string description)
        => JsonSerializer.Deserialize<T>(DecodeRaw(prefix, text, description), JsonOptions)
           ?? throw new InvalidOperationException($"The {description} is empty.");

    private static string DecodeRaw(string prefix, string text, string description)
    {
        var token = ExtractToken(prefix, text);
        var encoded = token[prefix.Length..].Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
        try
        {
            using var input = new MemoryStream(Convert.FromBase64String(encoded));
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > 1_000_000)
                    throw new InvalidOperationException($"The {description} is too large.");
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            throw new InvalidOperationException($"The {description} is damaged or incomplete.", ex);
        }
    }

    private static string ExtractToken(string prefix, string text)
    {
        var start = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            throw new InvalidOperationException($"The pasted text does not contain a {prefix.TrimEnd('-')} code.");
        var end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '"')
            end++;
        return text[start..end].Trim();
    }
}
