// Весь проект — только Linux: права файлов Unix, open(2) с O_NOFOLLOW,
// systemd и logind. Пометка сборки снимает с анализатора вопрос «а на
// Windows?» один раз — как у EliteSIP.Platform.Linux.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]
