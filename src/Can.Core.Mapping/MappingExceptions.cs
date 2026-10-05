namespace Can.Core.Mapping;

/// <summary>Çalışma anında eşleme yapılamadığında fırlatılır (ör. tanımsız map).</summary>
public class MappingException : InvalidOperationException
{
    public MappingException(string message)
        : base(message) { }

    public MappingException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>Profillerdeki tanımlar hatalı ya da eksik olduğunda fırlatılır.</summary>
public sealed class MappingConfigurationException : MappingException
{
    public MappingConfigurationException(string message)
        : base(message) { }
}
