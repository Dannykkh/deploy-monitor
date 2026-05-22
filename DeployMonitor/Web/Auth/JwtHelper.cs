using System;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace DeployMonitor.Web.Auth
{
    public class JwtHelper
    {
        private readonly byte[] _key;

        public JwtHelper()
        {
            var keyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "jwt-secret.key");
            if (File.Exists(keyPath))
            {
                _key = File.ReadAllBytes(keyPath);
            }
            else
            {
                _key = RandomNumberGenerator.GetBytes(32);
                File.WriteAllBytes(keyPath, _key);
            }
        }

        public SecurityKey GetSecurityKey() => new SymmetricSecurityKey(_key);

        public string GenerateToken(string username)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.Name, username),
            };

            var credentials = new SigningCredentials(GetSecurityKey(), SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(
                issuer: "DeployMonitor",
                audience: "DeployMonitor",
                claims: claims,
                expires: DateTime.UtcNow.AddDays(7),
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
