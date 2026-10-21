using System.Net;
using System.Net.Sockets;

namespace UltraLauncher.Core.Common;

/// <summary>Переводит произвольные исключения в понятную пользователю ошибку.</summary>
public static class ErrorTranslator
{
    private const int HResultDiskFull = unchecked((int)0x80070070);
    private const int HResultHandleDiskFull = unchecked((int)0x80070027);

    public static LauncherException Translate(Exception ex)
    {
        switch (ex)
        {
            case LauncherException le:
                return le;
            case OperationCanceledException:
                return new LauncherException(ErrorCode.Unknown, "Операция отменена.", null, ex);
            case HttpRequestException http when http.StatusCode is HttpStatusCode.NotFound:
                return new LauncherException(ErrorCode.Network,
                    "Сервер не нашёл запрошенный файл (HTTP 404).",
                    "Обновите список версий и повторите попытку.", ex);
            case HttpRequestException http when http.StatusCode is not null:
                return new LauncherException(ErrorCode.Network,
                    $"Сервер вернул ошибку HTTP {(int)http.StatusCode.Value}.",
                    "Повторите попытку позже.", ex);
            case HttpRequestException or SocketException or TimeoutException:
                return new LauncherException(ErrorCode.Network,
                    "Нет соединения с сервером загрузки.",
                    "Проверьте подключение к интернету. Уже установленные версии можно запустить в офлайн-режиме.", ex);
            case UnauthorizedAccessException:
                return new LauncherException(ErrorCode.AccessDenied,
                    "Нет доступа к файлу или папке.",
                    "Выберите папку игры, доступную для записи, или закройте программы, которые используют эти файлы.", ex);
            case IOException io when io.HResult is HResultDiskFull or HResultHandleDiskFull:
                return new LauncherException(ErrorCode.DiskFull,
                    "Недостаточно места на диске.",
                    "Освободите место или выберите папку игры на другом диске.", ex);
            case IOException:
                return new LauncherException(ErrorCode.AccessDenied,
                    "Ошибка чтения или записи файла: " + ex.Message,
                    "Проверьте, что файл не занят другой программой, и повторите попытку.", ex);
            default:
                return new LauncherException(ErrorCode.Unknown,
                    "Непредвиденная ошибка: " + ex.Message,
                    "Подробности — в разделе «Диагностика».", ex);
        }
    }

    /// <summary>Временная ли это сетевая ошибка, которую стоит повторить.</summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException http when http.StatusCode is { } code =>
            (int)code >= 500 || code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,
        HttpRequestException => true,
        SocketException => true,
        TimeoutException => true,
        HttpIOException => true,
        IOException io when io.InnerException is SocketException or HttpIOException => true,
        _ => false,
    };
}
