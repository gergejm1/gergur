using System.IO.Pipes;
using System.Text;

namespace Gergur.App;

/// <summary>
/// Keeps one Gergur per user, and gives later launches somewhere to send their url.
///
/// This is what makes being the default browser work: Windows answers a link click by
/// running "Gergur.exe &lt;url&gt;" whether or not Gergur is already up. Without the
/// hand-off, that second launch would surface the running window and silently drop the
/// link, which is also how every "sign in with..." page from a desktop app arrives.
/// </summary>
public static class SingleInstance
{
    // Per user: two people signed in at once each get their own browser.
    private static readonly string PipeName = "Gergur.Instance." + Environment.UserName;

    /// <summary>Listens for urls forwarded by later launches, for the life of the process.</summary>
    public static void StartListener(Action<string> onUrl)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string url = (await reader.ReadToEndAsync()).Trim();
                    if (url.Length > 0)
                        onUrl(url);
                }
                catch
                {
                    // A dropped connection must never take the browser down: wait, retry.
                    await Task.Delay(250);
                }
            }
        });
    }

    /// <summary>
    /// Hands a url to the instance already running. False when nobody answered, which
    /// is the caller's cue to fall back to just surfacing that window.
    /// </summary>
    public static bool ForwardUrl(string url, int timeoutMs = 3000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.Write(url);
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
