using System.Security.Cryptography;
using System.Text;

namespace AwardsFerm.Api.Auth;

public sealed class TokenEncryptionService
{
    private readonly byte[] _key;

    public TokenEncryptionService(IConfiguration configuration)
    {
        var configured = configuration["TokenEncryption:KeyBase64"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _key = Convert.FromBase64String(configured);
            if (_key.Length != 32)
                throw new InvalidOperationException("TokenEncryption:KeyBase64 must be 32-byte base64.");
            return;
        }

        var fallback = configuration["Auth:JwtSecret"] ?? "CHANGE_ME_MIN_32_CHARS_SECRET_12345";
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(fallback));
    }

    public string Encrypt(string plainText)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plainText);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);

        var payload = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, payload, nonce.Length + tag.Length, cipher.Length);
        return Convert.ToBase64String(payload);
    }

    public string Decrypt(string encrypted)
    {
        var payload = Convert.FromBase64String(encrypted);
        var nonce = payload[..12];
        var tag = payload[12..28];
        var cipher = payload[28..];
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
