using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AElf.Common;
using Xunit;

namespace AElf.Cryptography.Tests
{
    public class CryptoHelpersTests
    {
        [Fact]
        public void Test_Generate_Key()
        {
            CryptoHelpers.GenerateKeyPair();
        }
        
        [Fact]
        public void Test_Recover_Public_key()
        {
            var keyPair = CryptoHelpers.GenerateKeyPair();
            
            var messageBytes1 = Encoding.UTF8.GetBytes("Hello world.");
            var messageHash1 = SHA256.Create().ComputeHash(messageBytes1);
            
            var messageBytes2 = Encoding.UTF8.GetBytes("Hello aelf.");
            var messageHash2 = SHA256.Create().ComputeHash(messageBytes2);
            
            var signature1 = CryptoHelpers.SignWithPrivateKey(keyPair.PrivateKey, messageHash1);

            var recoverResult1 = CryptoHelpers.RecoverPublicKey(signature1, messageHash1, out var publicKey1);
            
            Assert.True(recoverResult1);
            Assert.True(publicKey1.BytesEqual(keyPair.PublicKey));
            
            var recoverResult2 = CryptoHelpers.RecoverPublicKey(signature1, messageHash2, out var publicKey2);
            
            Assert.True(recoverResult2);
            Assert.False(publicKey2.BytesEqual(keyPair.PublicKey));
        }
    }
}