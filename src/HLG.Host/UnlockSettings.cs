using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

// The secret is bound to the Windows user and never sent to the browser.
sealed class UnlockSettings
{
    public record State(bool Enabled = false, bool Automatic = false, bool UnlockWithoutConnection = false, string? UserSid = null, string? ProtectedPassword = null);
    readonly string path;
    readonly string sid;
    readonly object gate = new();
    readonly byte[] entropy;
    State? cached;
    public event Action? Changed;

    public UnlockSettings(string path, string? userSid = null) {
        this.path = path;
        sid = userSid ?? WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法确定当前 Windows 用户。");
        entropy = Encoding.UTF8.GetBytes("Neutrino HLG remote unlock v1/" + sid);
    }

    public State Read() {
        lock (gate) {
            if (cached is not null) return cached;
            byte[] contents;
            try { contents = File.ReadAllBytes(path); }
            catch (FileNotFoundException) { return cached = new State(); }
            catch (DirectoryNotFoundException) { return cached = new State(); }
            var state = JsonSerializer.Deserialize<State>(contents) ?? throw new InvalidDataException("远程解锁设置无效。");
            if (state.UserSid != sid || state.ProtectedPassword is { Length: > 16384 }) throw new InvalidDataException("远程解锁设置不属于当前 Windows 用户。");
            return cached = state;
        }
    }

    public void Save(bool enabled, bool automatic, bool withoutConnection, string? password, bool deletePassword = false) {
        lock (gate) {
            var old = Read();
            var encrypted = deletePassword ? null : old.ProtectedPassword;
            if (!string.IsNullOrEmpty(password)) {
                if (password.Length > 512) throw new ArgumentException("密码过长。");
                var bytes = Encoding.Unicode.GetBytes(password);
                try { encrypted = Convert.ToBase64String(ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser)); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            if (automatic && string.IsNullOrEmpty(encrypted)) throw new ArgumentException("自动解锁需要先设置 Windows 账户密码。");
            var next = new State(enabled, automatic, withoutConnection, sid, encrypted);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(next)); File.Move(temp, path, true); cached = next; }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        Changed?.Invoke();
    }

    // Callers must clear the returned bytes immediately after use.
    public byte[]? ReadPassword() {
        var state = Read();
        if (string.IsNullOrEmpty(state.ProtectedPassword)) return null;
        try { return ProtectedData.Unprotect(Convert.FromBase64String(state.ProtectedPassword), entropy, DataProtectionScope.CurrentUser); }
        catch (FormatException e) { throw new InvalidDataException("保存的密码数据无效，请重新设置。", e); }
    }
}
