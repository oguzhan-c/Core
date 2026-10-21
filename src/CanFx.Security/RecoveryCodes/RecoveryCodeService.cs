using System.Security.Cryptography;
using System.Text;

namespace CanFx.Security.RecoveryCodes;

/// <summary>Kullanıcıya bir kez gösterilecek kodlar ve veritabanında saklanacak hash'leri.</summary>
/// <param name="Codes">Kullanıcıya gösterilecek kodlar (<c>ABCDE-FGH23</c>); saklanmaz.</param>
/// <param name="CodeHashes">Saklanacak hash'ler (<c>RecoveryCode</c> kayıtları).</param>
public sealed record RecoveryCodeSet(IReadOnlyList<string> Codes, IReadOnlyList<string> CodeHashes);

/// <summary>
/// İki adımlı doğrulama kurtarma kodları: authenticator cihazı kaybolursa kullanıcı bunlardan biriyle (bir kez)
/// giriş yapar. 2FA açılınca üretilip kullanıcıya bir kez gösterilir.
/// </summary>
public interface IRecoveryCodeService
{
    /// <summary>Yeni kodlar (varsayılan 10). Eski kodları silip bunları kaydet.</summary>
    RecoveryCodeSet Generate(int count = 10);

    /// <summary>Kullanıcının girdiği kodun hash'i (büyük/küçük harf, tire ve boşluk fark etmez); kayıt bununla aranır.</summary>
    string Hash(string code);
}

/// <summary>
/// 10 karakterlik kodlar (karışan karakterler yok: I, O, 0, 1), ≈ 50 bit. Düz metin değil, gizli anahtarla
/// HMAC-SHA256'lanmış hâli saklanır: veritabanı sızsa bile kodlar anahtar bilinmeden denenemez.
/// </summary>
public sealed class RecoveryCodeService : IRecoveryCodeService
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int Length = 10;

    private readonly byte[] _key;

    public RecoveryCodeService(string secretKey)
    {
        if (Encoding.UTF8.GetByteCount(secretKey ?? string.Empty) < 32)
            throw new InvalidOperationException("Kurtarma kodu anahtarı en az 32 bayt olmalı.");

        // Doğrulama kodlarıyla aynı anahtar verilse bile kullanım amacına göre ayrı anahtar türetilir
        _key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretKey!), "can-recovery-codes"u8);
    }

    public RecoveryCodeSet Generate(int count = 10)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 50);

        var codes = new List<string>(count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (codes.Count < count)
        {
            string raw = RandomNumberGenerator.GetString(Alphabet, Length);
            if (seen.Add(raw))
                codes.Add($"{raw[..5]}-{raw[5..]}");
        }

        return new RecoveryCodeSet(codes, codes.Select(Hash).ToArray());
    }

    public string Hash(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        string normalized = new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(normalized)));
    }
}
