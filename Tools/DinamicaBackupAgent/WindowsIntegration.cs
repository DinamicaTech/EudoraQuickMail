using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace DinamicaBackupAgent;

internal static class Secrets
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr buffer);
    public static void Save(string name, string user, string password)
    {
        var bytes = Encoding.Unicode.GetBytes(password);
        var pointer = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var value = new Credential { Type = 1, TargetName = "DinamicaBackup/" + name, UserName = user, CredentialBlob = pointer, CredentialBlobSize = (uint)bytes.Length, Persist = 2 };
            if (!CredWrite(ref value, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(pointer, i, 0); Marshal.FreeCoTaskMem(pointer); Array.Clear(bytes); }
    }
    public static (string User, string Password) Read(string name)
    {
        if (!CredRead("DinamicaBackup/" + name, 1, 0, out var pointer)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Credential is unavailable for the Windows task identity: " + name);
        try { var value = Marshal.PtrToStructure<Credential>(pointer); return (value.UserName, Marshal.PtrToStringUni(value.CredentialBlob, (int)value.CredentialBlobSize / 2) ?? ""); }
        finally { CredFree(pointer); }
    }
}
internal static class NasConnection
{
    public static void ConnectDestinations(DinamicaBackup.BackupJob job)
    {
        Connect(job.NasShare, job.NasCredential);
        if (job.CopyAttachments) Connect(job.AttachmentNasShare, job.AttachmentNasCredential);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Resource { public uint Scope, Type, DisplayType, Usage; public string? LocalName; public string RemoteName; public string? Comment, Provider; }
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetAddConnection2(ref Resource resource, string password, string username, uint flags);
    public static void Connect(string share, string credential)
    {
        if (string.IsNullOrWhiteSpace(share) || string.IsNullOrWhiteSpace(credential)) return;
        if (!share.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("NAS share must be a UNC path.");
        var secret = Secrets.Read(credential); var resource = new Resource { Type = 1, RemoteName = share };
        var result = WNetAddConnection2(ref resource, secret.Password, secret.User, 0);
        if (result != 0) throw new Win32Exception(result, result == 1219 ? "Windows already has a connection to this NAS using a different identity. Existing connections were left intact." : "Cannot connect to NAS.");
    }
}
internal static class WindowsScheduler
{
    public static void Register(string jobPath, string jobId, string? user = null, string? password = null)
    {
        var stage = "Resolve Windows account";
        object? scheduler = null;
        try
        {
            user = WindowsTaskIdentity.Resolve(user);
            if (user.Length > 0 && string.IsNullOrEmpty(password))
                throw new ArgumentException("Enter this Windows account's password, not its PIN. To use the current interactive session, leave both username and password blank.");
            stage = "Activate Windows Task Scheduler";
            var type = Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException("Windows Task Scheduler COM registration is unavailable.");
            scheduler = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Windows Task Scheduler could not be instantiated.");
            dynamic service = scheduler;
            stage = "Connect to Windows Task Scheduler"; service.Connect();
            stage = "Open the task root folder"; dynamic root = service.GetFolder("\\");
            stage = "Create an in-memory task definition"; dynamic task = service.NewTask(0);
            stage = "Set task description"; task.RegistrationInfo.Description = "Dinamica Backup Agent: scheduled backup, maintenance and restore tests.";
            stage = "Configure task execution settings"; dynamic settings = task.Settings;
            settings.MultipleInstances = 2;
            settings.StartWhenAvailable = true;
            settings.DisallowStartIfOnBatteries = false; settings.StopIfGoingOnBatteries = false;
            settings.ExecutionTimeLimit = "PT0S";
            stage = "Create the daily trigger"; dynamic trigger = task.Triggers.Create(2);
            trigger.StartBoundary = DateTime.Now.AddMinutes(1).ToString("yyyy-MM-ddTHH:mm:ss");
            trigger.DaysInterval = 1;
            stage = "Configure five-minute repetition"; dynamic repetition = trigger.Repetition;
            repetition.Interval = "PT5M"; repetition.Duration = "P1D";
            stage = "Configure the agent executable"; dynamic action = task.Actions.Create(0);
            action.Path = Environment.ProcessPath!;
            action.Arguments = "/scheduled /job \"" + jobPath.Replace("\"", "") + "\"";
            action.WorkingDirectory = AppContext.BaseDirectory;
            stage = "Configure task Windows identity";
            dynamic principal = task.Principal;
            principal.UserId = string.IsNullOrWhiteSpace(user) ? System.Security.Principal.WindowsIdentity.GetCurrent().Name : user;
            principal.LogonType = string.IsNullOrWhiteSpace(user) ? 3 : 1;
            stage = "Validate task definition (no task changes)";
            root.RegisterTaskDefinition("DinamicaBackup-" + jobId, task, 1, principal.UserId, null, principal.LogonType, null);
            stage = string.IsNullOrWhiteSpace(user)
                ? "Register task for the current interactive Windows user"
                : "Register task with supplied Windows credentials";
            if (string.IsNullOrWhiteSpace(user)) root.RegisterTaskDefinition("DinamicaBackup-" + jobId, task, 6, null, null, 3, null);
            else root.RegisterTaskDefinition("DinamicaBackup-" + jobId, task, 6, user, password, 1, null);
        }
        catch (Exception ex)
        {
            // Record only controlled context, type and HRESULT: never credentials, COM arguments
            // or exception messages that could echo user-supplied values.
            var code = $"0x{ex.HResult:X8}";
            var log = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(jobPath))!, "backup-agent-registration-error.log");
            var logStatus = "Diagnostic log: " + log;
            try
            {
                System.IO.File.AppendAllText(log, $"{DateTimeOffset.Now:O} | stage={stage} | HRESULT={code} | exception={ex.GetType().FullName} | processArchitecture={RuntimeInformation.ProcessArchitecture} | OSArchitecture={RuntimeInformation.OSArchitecture} | runtime={RuntimeInformation.FrameworkDescription} | agentVersion={typeof(WindowsScheduler).Assembly.GetName().Version} | logonMode={(string.IsNullOrWhiteSpace(user) ? "Interactive" : "Password")}\n");
            }
            catch { logStatus = "The diagnostic log could not be written. Copy the details below."; }
            var advice = stage == "Resolve Windows account" ? ex.Message : ex.HResult switch
            {
                unchecked((int)0x80040154) => "Windows reported a missing COM class. This is not a password-validation error. Check whether Task Scheduler opens normally and provide the stage and code below for diagnosis.",
                unchecked((int)0x80070005) => "Windows denied permission. Check task-registration permissions and the selected Windows identity.",
                unchecked((int)0x8007052E) => "Windows rejected the logon. Use a Windows account password, not a PIN or NAS password.",
                unchecked((int)0x80070569) => "The selected Windows account needs the 'Log on as a batch job' right.",
                unchecked((int)0x80070057) => "Windows rejected a registration parameter. The log records whether account resolution and task-definition validation succeeded. Verify that the password belongs to the resolved Windows account, not another local account.",
                _ => "Check Windows Task Scheduler and the selected Windows identity. Provide the stage and code below for diagnosis."
            };
            throw new InvalidOperationException("Windows task registration could not be completed.\n\nStep: " + stage + "\nWindows error: " + code + "\n\n" + advice + "\n\n" + logStatus, ex);
        }
        finally
        {
            if (scheduler != null && Marshal.IsComObject(scheduler))
            {
                try { Marshal.FinalReleaseComObject(scheduler); }
                catch { /* Do not hide the original registration failure with cleanup errors. */ }
            }
        }
    }
}
