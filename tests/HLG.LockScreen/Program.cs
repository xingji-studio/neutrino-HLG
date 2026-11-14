using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.IO.Pipes;
using System.Text;
using Microsoft.AspNetCore.Http;

var dir = Path.Combine(AppContext.BaseDirectory, "unlock-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try {
    var path = Path.Combine(dir, "settings.json");
    var settings = new UnlockSettings(path);
    if (settings.Read().Enabled) throw new Exception("Missing settings must be disabled.");
    try { settings.Save(true, true, false, null); throw new Exception("Automatic mode accepted a missing password."); }
    catch (ArgumentException) { }
    const string password = "test-密码-🔒";
    using var request = SecureSecretJson.CreateRequest(password, new[] { "test" });
    var encoded = request.ToArray(); CryptographicOperations.ZeroMemory(request.GetBuffer());
    if (!SecureSecretJson.TryReadPassword(encoded, out var parsed, out var parsedLength) || parsed is null || !parsed.AsSpan(0, parsedLength).SequenceEqual(password)) throw new Exception("Escaped or Unicode password was not decoded correctly.");
    CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(parsed.AsSpan())); CryptographicOperations.ZeroMemory(encoded);
    var oversized = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { password = new string('x', 513) });
    if (SecureSecretJson.TryReadPassword(oversized, out _, out _)) throw new Exception("Oversized password was accepted.");
    CryptographicOperations.ZeroMemory(oversized);
    var field = new System.Drawing.RectangleF(600, 400, 280, 40);
    if (!SecureLoginPolicy.IsNearby(field, new System.Drawing.RectangleF(660, 350, 160, 30))) throw new Exception("The account label above the password field was not recognized.");
    if (SecureLoginPolicy.IsNearby(field, new System.Drawing.RectangleF(10, 350, 160, 30)) || SecureLoginPolicy.IsNearby(field, new System.Drawing.RectangleF(660, 500, 160, 30))) throw new Exception("An unrelated account label was accepted.");
    settings.Save(true, true, false, password);
    if (File.ReadAllText(path).Contains(password, StringComparison.Ordinal)) throw new Exception("Password was stored in plaintext.");
    var saved = settings.Read();
    if (!saved.Enabled || !saved.Automatic || saved.UnlockWithoutConnection || saved.ProtectedPassword is null) throw new Exception("Settings did not round-trip.");
    var bytes = settings.ReadPassword() ?? throw new Exception("Password was not saved.");
    try { if (Encoding.Unicode.GetString(bytes) != password) throw new Exception("Password did not round-trip."); }
    finally { CryptographicOperations.ZeroMemory(bytes); }
    settings.Save(true, false, false, null, true);
    if (settings.ReadPassword() is not null) throw new Exception("Password was not removed.");
    try { _ = new UnlockSettings(path, "S-1-5-21-123-456-789-1234").Read(); throw new Exception("A different account accepted the settings."); }
    catch (InvalidDataException) { }

    // Exercise the actual Windows pipe rights and impersonation handshake with
    // the current user; no service installation or workstation lock is needed.
    var pipeName = "HLG.LockScreen.Test." + Guid.NewGuid().ToString("N");
    var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
    using var identity = WindowsIdentity.GetCurrent();
    security.AddAccessRule(new PipeAccessRule(identity.User!, SecureService.ClientRights, AccessControlType.Allow));
    using var server = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
    var wait = server.WaitForConnectionAsync();
    using var client = new NamedPipeClientStream(".", pipeName, SecureService.ClientRights, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation, HandleInheritability.None);
    await client.ConnectAsync(3000); await wait.WaitAsync(TimeSpan.FromSeconds(3));
    await client.WriteAsync(new byte[] { (byte)'H' });
    var hello = new byte[1]; await server.ReadExactlyAsync(hello);
    if (hello[0] != (byte)'H') throw new Exception("Pipe handshake failed.");
    SecurityIdentifier? impersonated = null;
    server.RunAsClient(() => { using var current = WindowsIdentity.GetCurrent(true); impersonated = current?.User; });
    if (impersonated != identity.User) throw new Exception("Pipe client identity was not verified.");
    try {
        using var second = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        throw new Exception("A client can create another server pipe instance.");
    } catch (UnauthorizedAccessException) { }
    await server.WriteAsync(new byte[] { (byte)'Y' });
    await client.ReadExactlyAsync(hello);
    if (hello[0] != (byte)'Y') throw new Exception("Pipe acknowledgement failed.");
    if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true) {
        try { SecureService.EnsureSystem(); throw new Exception("A non-SYSTEM process was allowed to launch the worker."); }
        catch (UnauthorizedAccessException) { }
        var workerName = "HLG.Worker.Test." + Guid.NewGuid().ToString("N");
        using var workerServer = NamedPipeServerStreamAcl.Create(workerName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, SecureService.WorkerSecurity());
        using var unauthorized = new NamedPipeClientStream(".", workerName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await unauthorized.ConnectAsync(1000); throw new Exception("A non-SYSTEM process connected to the worker pipe."); }
        catch (UnauthorizedAccessException) { }
    }
    var http = new DefaultHttpContext(); http.Request.Scheme = "http"; http.Request.Host = new HostString("example.test"); http.Request.Headers.Origin = "http://example.test";
    await SecureScreenSession.Handle(http, null!);
    if (http.Response.StatusCode != 403) throw new Exception("Secure desktop accepted an HTTP connection.");
    var crossOrigin = new DefaultHttpContext(); crossOrigin.Request.Scheme = "https"; crossOrigin.Request.Host = new HostString("example.test"); crossOrigin.Request.Headers.Origin = "https://other.test";
    await SecureScreenSession.Handle(crossOrigin, null!);
    if (crossOrigin.Response.StatusCode != 403) throw new Exception("Secure desktop accepted a cross-origin connection.");
    var state = SessionLock.Query(SessionLock.CurrentSessionId);
    if (state == SessionLockState.Unknown) throw new Exception("Current Windows session state is unknown.");
    Console.WriteLine("Encrypted settings, restricted pipe handshake, impersonation and Windows session query passed: " + state);
} finally { Directory.Delete(dir, true); }
