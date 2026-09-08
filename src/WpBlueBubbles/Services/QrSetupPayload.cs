using System;
using Windows.Data.Json;

namespace WpBlueBubbles.Services
{
    public sealed class QrSetupPayload
    {
        public string Address { get; private set; }
        public string Password { get; private set; }

        public static bool TryParse(string value, out QrSetupPayload payload, out string error)
        {
            payload = null;
            error = null;

            JsonArray values;
            if (string.IsNullOrWhiteSpace(value) || !JsonArray.TryParse(value.Trim(), out values) || values.Count < 2)
            {
                error = "That is not a valid server QR setup code.";
                return false;
            }

            var password = values[0].ValueType == JsonValueType.String ? values[0].GetString() : string.Empty;
            var address = values[1].ValueType == JsonValueType.String ? values[1].GetString() : string.Empty;
            Uri parsedAddress;
            if (string.IsNullOrWhiteSpace(password) || !Uri.TryCreate(address, UriKind.Absolute, out parsedAddress))
            {
                error = "The QR setup code does not include a valid server URL and password.";
                return false;
            }

            payload = new QrSetupPayload { Address = address.Trim(), Password = password.Trim() };
            return true;
        }
    }
}
