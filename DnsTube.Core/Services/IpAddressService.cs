using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

using DnsTube.Core.Enums;
using DnsTube.Core.Interfaces;
using DnsTube.Core.Models;

using Microsoft.Extensions.Logging;

namespace DnsTube.Core.Services
{
	public class IpAddressService : IIpAddressService
	{
		private readonly ISettingsService _settingsService;
		private readonly ILogService _logService;
		private readonly IHttpClientFactory _httpClientFactory;

		public IpAddressService(ISettingsService settingsService, ILogService logService, IHttpClientFactory httpClientFactory)
		{
			_settingsService = settingsService;
			_logService = logService;
			_httpClientFactory = httpClientFactory;
		}

		public async Task<string?> GetPublicIpAddressAsync(IpSupport protocol)
		{
			string? publicIpAddress = null;
			var maxAttempts = 3;

			var settings = await _settingsService.GetAsync();
			var url = protocol == IpSupport.IPv4 ? settings.IPv4_API : settings.IPv6_API;

			HttpClient httpClient;

			if (protocol == IpSupport.IPv4){
				httpClient = _httpClientFactory.CreateClient(HttpClientName.IpAddressV4.ToString());
			}
			else
			{
				httpClient = new HttpClient(new SocketsHttpHandler()
				{
					ConnectCallback = async (context, cancellationToken) =>
					{
						// Use DNS to look up the IP addresses of the target host:
						// - IP v4: AddressFamily.InterNetwork
						// - IP v6: AddressFamily.InterNetworkV6
						// - IP v4 or IP v6: AddressFamily.Unspecified
						// note: this method throws a SocketException when there is no IP address for the host
						var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, AddressFamily.InterNetworkV6, cancellationToken);

						// Open the connection to the target host/port
						var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

						// Turn off Nagle's algorithm since it degrades performance in most HttpClient scenarios.
						socket.NoDelay = true;

						try
						{
							await socket.ConnectAsync(entry.AddressList, context.DnsEndPoint.Port, cancellationToken);

							// If you want to choose a specific IP address to connect to the server
							// await socket.ConnectAsync(
							//    entry.AddressList[Random.Shared.Next(0, entry.AddressList.Length)],
							//    context.DnsEndPoint.Port, cancellationToken);

							// Return the NetworkStream to the caller
							return new NetworkStream(socket, ownsSocket: true);
						}
						catch
						{
							socket.Dispose();
							throw;
						}
					}
				});
			}

			for (var attempts = 0; attempts < maxAttempts; attempts++)
			{
				try
				{
					var response = await httpClient.GetStringAsync(url);
					var candidatePublicIpAddress = response.Replace("\n", "");

					if (!IsValidIpAddress(protocol, candidatePublicIpAddress))
					{
						if (protocol == IpSupport.IPv6)
						{
							return string.Empty;
						}

						throw new Exception($"Malformed response, expected IP address: {response}");
					}

					publicIpAddress = candidatePublicIpAddress;

					await SaveIpAddressIfChanged(protocol, publicIpAddress, settings);
					break;
				}
				catch (Exception e)
				{
					if (attempts >= maxAttempts - 1)
					{
						await _logService.WriteAsync(e.Message, LogLevel.Error);
					}
				}
			}

			return publicIpAddress;
		}

		private async Task SaveIpAddressIfChanged(IpSupport protocol, string ipAddress, ISettings settings)
		{
			if (protocol == IpSupport.IPv4 && settings.PublicIpv4Address != ipAddress)
			{
				settings.PublicIpv4Address = ipAddress;
				await _settingsService.SaveAsync(settings);
			}
			else if (protocol == IpSupport.IPv6 && settings.PublicIpv6Address != ipAddress)
			{
				settings.PublicIpv6Address = ipAddress;
				await _settingsService.SaveAsync(settings);
			}
		}

		public bool IsValidIpAddress(IpSupport protocol, string ipString)
		{
			if (string.IsNullOrWhiteSpace(ipString))
				return false;

			if (protocol == IpSupport.IPv4)
			{
				// probably a more efficient way to parse than using regex
				string[] splitValues = ipString.Split('.');
				if (splitValues.Length != 4)
					return false;

				return splitValues.All(r => byte.TryParse(r, out byte tempForParsing));
			}
			else
			{
				var regex = new Regex(Application.Ipv6RegexExact);
				var match = regex.Match(ipString);
				return match.Success;
			}
		}

		public List<NetworkAdapter> GetNetworkAdapters()
		{
			var adapters = new List<NetworkAdapter>();
			var candidateAdapters = NetworkInterface.GetAllNetworkInterfaces();

			foreach (var adapter in candidateAdapters)
			{
				var interNetworkAddresses = adapter
					.GetIPProperties().UnicastAddresses
					.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

				if (interNetworkAddresses.Any())
				{
					adapters.Add(new NetworkAdapter()
					{
						Name = adapter.Name,
						IpAddress = interNetworkAddresses.First().Address.ToString()
					});
				}
			}

			return adapters;
		}
	}
}
