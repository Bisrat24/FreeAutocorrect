using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace FreeAutocorrect.Services;

public class SingleInstanceManager : IDisposable
{
    private const string MutexName = "FreeAutocorrect_SingleInstance_Mutex_Bisrat";
    private const string PipeName = "FreeAutocorrect_Activation_Pipe";
    
    private Mutex? _mutex;
    private bool _hasHandle = false;
    private CancellationTokenSource? _pipeCts;

    public event Action? OnActivateRequested;

    public bool CheckIsFirstInstance()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            _hasHandle = createdNew;

            if (!_hasHandle)
            {
                // Signal the first instance to bring its window to front
                SignalExistingInstance();
                return false;
            }

            // Start listening for activation signals from future instances
            StartPipeServer();
            return true;
        }
        catch (AbandonedMutexException)
        {
            _hasHandle = true;
            StartPipeServer();
            return true;
        }
        catch
        {
            return true;
        }
    }

    public void ShowAlreadyRunningMessage()
    {
        System.Windows.MessageBox.Show(
            "FreeAutocorrect is already running and docked in your system tray.\n\n" +
            "Only one instance can run at a time to monitor your typing efficiently.\n\n" +
            "Look for the FreeAutocorrect icon in your taskbar notification area, or press Ctrl+Shift+G to check grammar.",
            "FreeAutocorrect Already Running",
            MessageBoxButton.OK,
            MessageBoxImage.Information
        );
    }

    private void SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.WriteLine("ACTIVATE");
            writer.Flush();
        }
        catch
        {
            // Ignore if pipe connection times out
        }
    }

    private void StartPipeServer()
    {
        _pipeCts = new CancellationTokenSource();
        Task.Run(async () =>
        {
            while (!_pipeCts.Token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(_pipeCts.Token);

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var message = await reader.ReadLineAsync();

                    if (message == "ACTIVATE")
                    {
                        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            OnActivateRequested?.Invoke();
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(1000);
                }
            }
        });
    }

    public void Dispose()
    {
        try
        {
            _pipeCts?.Cancel();
            _pipeCts?.Dispose();
            if (_hasHandle && _mutex != null)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch { }
                _mutex.Dispose();
                _mutex = null;
            }
        }
        catch { }
    }
}
