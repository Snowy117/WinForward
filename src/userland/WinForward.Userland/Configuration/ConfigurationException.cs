namespace WinForward.Userland.Configuration;

/// <summary>
/// 配置文件无法解析成 <see cref="Policy.PolicyTable"/> / <see cref="ControllerConfiguration"/> 时抛出。消息里必须带 JSON 路径
/// （例如 <c>rules[2].remotePorts</c>），用户才知道改哪一行。
/// </summary>
public sealed class ConfigurationException : Exception
{
    public ConfigurationException(string message)
        : base(message)
    {
    }

    public ConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
