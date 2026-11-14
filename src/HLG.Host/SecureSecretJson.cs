using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

static class SecureSecretJson
{
    // The caller must clear the returned stream's underlying buffer.
    public static MemoryStream CreateRequest(ReadOnlySpan<char> password, IEnumerable<string> accounts) {
        var stream = new MemoryStream();
        try {
            using (var writer = new Utf8JsonWriter(stream)) {
                writer.WriteStartObject(); writer.WriteString("type", "auto"); writer.WritePropertyName("password"); writer.WriteStringValue(password);
                writer.WriteStartArray("accounts"); foreach (var account in accounts) writer.WriteStringValue(account); writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();
            }
            return stream;
        } catch { CryptographicOperations.ZeroMemory(stream.GetBuffer()); stream.Dispose(); throw; }
    }
    // Avoid materializing the Windows password as an immutable managed string.
    public static bool TryReadPassword(ReadOnlySpan<byte> json, out char[]? password, out int length) {
        password = null; length = 0;
        var buffer = new char[512];
        try {
            var reader = new Utf8JsonReader(json); var found = false;
            while (reader.Read()) {
                if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("password")) continue;
                if (found || !reader.Read() || reader.TokenType != JsonTokenType.String) return false;
                found = true; length = reader.CopyString(buffer);
            }
            if (!found || length == 0) return false;
            password = buffer; buffer = []; return true;
        } catch (Exception e) when (e is JsonException or ArgumentException) { return false; }
        finally { CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan())); }
    }
}
