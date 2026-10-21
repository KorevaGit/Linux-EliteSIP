// Весь проект — только Linux: права файлов Unix, flock, D-Bus сеанса, X11.
// Пометка сборки снимает с анализатора вопрос «а на Windows?» один раз, а не
// на каждом вызове File.SetUnixFileMode.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]
