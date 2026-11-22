using Microsoft.IdentityModel.Tokens;
//using Newtonsoft.Json;
//using System.Text;

//namespace MobileShop.Services
//{
//    public class TwoCTwoPServiceExample
//    {
//    }
//}

using System.Net.Http;
using Newtonsoft.Json;
using JWT;		//https://github.com/jwt-dotnet/jwt
using JWT.Algorithms;
using JWT.Serializers;
using JWT.Exceptions;

var payload = new Dictionary<string, object>
{
  { "merchantID", "JT01" },
  { "invoiceNo", "1523953661" },
  { "description", "item 1" },
  { "amount", 1000.00 },
  { "currencyCode", "SGD" }
};

string secretKey = "ECC4E54DBA738857B84A7EBC6B5DC7187B8DA68750E88AB53AAA41F548D6F2D9";  //Merchant SHA Key

IJwtAlgorithm algorithm = new HMACSHA256Algorithm(); // symmetric
IJsonSerializer serializer = new JsonNetSerializer();
IBase64UrlEncoder urlEncoder = new JwtBase64UrlEncoder();
IJwtEncoder encoder = new JwtEncoder(algorithm, serializer, urlEncoder);

var jwtToken = encoder.Encode(payload, secretKey);

var requestData = new Dictionary<string, string>();
requestData.Add("payload", jwtToken);
string requestString = JsonConvert.SerializeObject(requestData);

using var client = new HttpClient();
client.DefaultRequestHeaders.Add("Accept", "text/plain");

var content = new StringContent(requestString, Encoding.UTF8, "application/json");
var response = await client.PostAsync("api_endpoint", content);

string responseData = await response.Content.ReadAsStringAsync();
