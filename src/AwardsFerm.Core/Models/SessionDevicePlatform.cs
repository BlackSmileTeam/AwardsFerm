namespace AwardsFerm.Core.Models;

/// <summary>Платформа/тип устройства для сессии.</summary>
public enum SessionDevicePlatform
{
    Random,
    /// <summary>Без эмуляции устройства — параметры браузера как на хосте.</summary>
    Native,
    Desktop,
    Laptop,
    MacBook,
    Tablet,
    AndroidPhone,
    IPhone
}
