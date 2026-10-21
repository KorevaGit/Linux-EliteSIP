using System.Net.Sockets;
using System.Text;
using EliteSIP.Platform.Linux.Native;

namespace EliteSIP.Platform.Linux;

/// <summary>
/// Один софтфон на пользователя — Linux-половина <c>Shell/SingleInstance</c>
/// Windows-версии.
/// </summary>
///
/// <remarks>
/// <para>
/// Зачем это вообще нужно, сказано в Windows-версии: две копии пишут один
/// <c>settings.json</c>, дважды регистрируют добавочный и дерутся за
/// микрофон. Здесь — только чем это сделано на Linux.
/// </para>
/// <para>
/// <b>Замок — <c>flock</c> на файл, а не именованный <see cref="Mutex"/>.</b>
/// Именованный <see cref="Mutex"/> в .NET на Unix есть, а именованного
/// <see cref="EventWaitHandle"/>, которым Windows будит первую копию, нет
/// (<see cref="PlatformNotSupportedException"/>). Раз будить всё равно своим
/// способом, замок тоже берётся системный: <c>flock</c> снимает ядро, когда
/// процесс умирает, — упавшая копия не оставляет за собой «уже запущено», как
/// оставил бы pid-файл.
/// </para>
/// <para>
/// <b>Будит Unix-сокет рядом с замком.</b> Вторая копия подключается и пишет
/// одну строку, первая показывает панель. Сокет лежит в
/// <c>$XDG_RUNTIME_DIR</c> — каталоге сеанса этого пользователя с правами
/// <c>0700</c>: чужой пользователь машины до него не дотянется, а после выхода
/// из сеанса каталог чистит logind.
/// </para>
/// <para>
/// <b>Замок на пользователя, а не на машину</b> — по той же причине, что на
/// Windows: за машиной может работать двое, у каждого свой добавочный.
/// </para>
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    private const string LockName = "instance.lock";
    private const string SocketName = "instance.sock";

    /// <summary>Что вторая копия говорит первой.</summary>
    internal const string ShowCommand = "show";

    /// <summary>
    /// Сколько вторая копия ждёт, пока первая начнёт слушать.
    /// </summary>
    ///
    /// <remarks>
    /// Окно между захватом замка и открытием сокета — доли секунды, но в него
    /// попадает именно то, ради чего класс есть: двойной щелчок по ярлыку.
    /// Две секунды — с запасом на медленный диск; дольше вторая копия висеть
    /// не должна, первая могла и зависнуть.
    /// </remarks>
    private static readonly TimeSpan ConnectPatience = TimeSpan.FromSeconds(2);

    private readonly SafeLockFile _lock;
    private readonly Socket _listener;
    private readonly string _socketPath;
    private readonly CancellationTokenSource _watching = new();

    private SingleInstance(SafeLockFile lockFile, Socket listener, string socketPath)
    {
        _lock = lockFile;
        _listener = listener;
        _socketPath = socketPath;
    }

    /// <summary>Каталог сеанса для замка и сокета.</summary>
    ///
    /// <remarks>
    /// Без <c>$XDG_RUNTIME_DIR</c> (вход без logind, редкие минимальные
    /// системы) — каталог в профиле: путь сокета ограничен 108 байтами, и
    /// <c>~/.cache</c> в него укладывается.
    /// </remarks>
    internal static string DefaultDirectory
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return string.IsNullOrEmpty(runtime)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "EliteSIP", "run")
                : Path.Combine(runtime, "elitesip");
        }
    }

    /// <summary>
    /// Занимает место единственной копии.
    /// </summary>
    ///
    /// <remarks>
    /// <see langword="null"/> — «мы вторые»: первой уже сказано показаться
    /// (или сказать не вышло — это пишется в <paramref name="log"/>), и надо
    /// просто выйти.
    /// </remarks>
    internal static SingleInstance? Claim(Action<string> log, string? directory = null)
    {
        directory ??= DefaultDirectory;
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var socketPath = Path.Combine(directory, SocketName);
        var lockFile = SafeLockFile.TryAcquire(Path.Combine(directory, LockName));

        if (lockFile is null)
        {
            Wake(socketPath, log);
            return null;
        }

        try
        {
            // Сокет от прежней копии, умершей без уборки. Замок у нас, значит,
            // слушать его некому.
            File.Delete(socketPath);

            Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(4);

            return new SingleInstance(lockFile, listener, socketPath);
        }
        catch
        {
            lockFile.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Начинает слушать, не постучится ли вторая копия.
    /// </summary>
    ///
    /// <param name="showPanel">
    /// чем показать панель. Зовётся не в потоке интерфейса — как и на Windows,
    /// перебрасывать в него дело вызывающего.
    /// </param>
    internal void Watch(Action showPanel, Action<string> log)
    {
        _ = Task.Run(async () =>
        {
            while (!_watching.IsCancellationRequested)
            {
                try
                {
                    using var client = await _listener.AcceptAsync(_watching.Token).ConfigureAwait(false);
                    if (await ReadCommandAsync(client, _watching.Token).ConfigureAwait(false) == ShowCommand)
                    {
                        showPanel();
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException error)
                {
                    log($"одна копия: приём побудки не удался: {error.SocketErrorCode}");
                }
            }
        });
    }

    public void Dispose()
    {
        _watching.Cancel();
        _listener.Dispose();

        // Сначала сокет, потом замок: иначе следующая копия могла бы взять
        // замок, открыть свой сокет — и потерять его под нашим удалением.
        try
        {
            File.Delete(_socketPath);
        }
        catch (IOException)
        {
        }

        _lock.Dispose();
        _watching.Dispose();
    }

    private static void Wake(string socketPath, Action<string> log)
    {
        var deadline = DateTime.UtcNow + ConnectPatience;

        while (true)
        {
            try
            {
                using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.Connect(new UnixDomainSocketEndPoint(socketPath));
                socket.Send(Encoding.ASCII.GetBytes(ShowCommand + "\n"));
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                // Первая копия взяла замок, но ещё не слушает. Ждём её, а не
                // запускаемся рядом: замок занят, значит, она жива.
                Thread.Sleep(50);
            }
            catch (SocketException error)
            {
                log($"одна копия: первая не ответила на побудку ({error.SocketErrorCode})");
                return;
            }
        }
    }

    private static async Task<string> ReadCommandAsync(Socket client, CancellationToken cancellation)
    {
        // Команда короткая, и ждать её дольше секунды незачем: подключиться и
        // молчать может только что-то, что не наша вторая копия.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));

        var buffer = new byte[32];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await client.ReceiveAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
            if (Array.IndexOf(buffer, (byte)'\n', 0, length) >= 0)
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(buffer, 0, length).Trim();
    }

    /// <summary>Файл под <c>flock(LOCK_EX)</c>, отпускаемый закрытием.</summary>
    private sealed class SafeLockFile : IDisposable
    {
        private readonly FileStream _file;

        private SafeLockFile(FileStream file)
        {
            _file = file;
        }

        /// <summary>
        /// <c>EWOULDBLOCK</c>: так <see cref="IOException.HResult"/> на Linux
        /// говорит «файл под чужим <c>flock</c>» (замер 21 октября 2026, .NET 10).
        /// </summary>
        private const int WouldBlock = 11;

        /// <remarks>
        /// <para>
        /// Замка здесь два слоя, и оба нужны. .NET на Unix сам берёт
        /// <c>flock</c> при открытии: с <see cref="FileShare.None"/> —
        /// исключительный, и если он занят, открытие бросает
        /// <see cref="IOException"/>. Но эту
        /// блокировку выключает переменная
        /// <c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING</c>, а одна копия не должна
        /// зависеть от окружения, которое ей передали, — поэтому следом
        /// <c>flock</c> зовётся и сам. На том же дескрипторе он ничего не
        /// меняет, а без блокировки .NET становится единственным замком.
        /// </para>
        /// </remarks>
        internal static SafeLockFile? TryAcquire(string path)
        {
            FileStream file;
            try
            {
                file = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
            }
            catch (IOException error) when (error.HResult == WouldBlock)
            {
                return null;
            }

            if (Libc.Flock(file.SafeFileHandle, Libc.LockExclusive | Libc.LockNonBlocking) == 0)
            {
                return new SafeLockFile(file);
            }

            file.Dispose();
            return null;
        }

        public void Dispose() => _file.Dispose();
    }
}
