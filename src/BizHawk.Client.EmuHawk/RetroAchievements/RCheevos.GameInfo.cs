using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;

using BizHawk.Common.CollectionExtensions;

namespace BizHawk.Client.EmuHawk
{
	public partial class RCheevos
	{
		private readonly RCheevosGameInfoForm _gameInfoForm = new();

		private ConsoleID _consoleId;

		private string _gameHash;
		private readonly Dictionary<string, int> _cachedGameIds = new(); // keep around IDs per hash to avoid unneeded API calls for a simple RebootCore

		private GameData _gameData;
		private readonly Dictionary<int, GameData> _cachedGameDatas = new(); // keep game data around to avoid unneeded API calls for a simple RebootCore

		public sealed class UserUnlocksRequest : RCheevoHttpRequest
		{
			private LibRCheevos.rc_api_fetch_user_unlocks_request_t _apiParams;
			private readonly IReadOnlyDictionary<int, Cheevo> _cheevos;
			private readonly Action _activeModeCallback;

			protected override void ResponseCallback(byte[] serv_resp)
			{
				var res = _lib.rc_api_process_fetch_user_unlocks_response(out var resp, serv_resp);
				if (res == LibRCheevos.rc_error_t.RC_OK)
				{
					unsafe
					{
						var unlocks = (int*)resp.achievement_ids;
						for (var i = 0; i < resp.num_achievement_ids; i++)
						{
							if (_cheevos.TryGetValue(unlocks![i], out var cheevo))
							{
								cheevo.SetUnlocked(_apiParams.hardcore, true);
							}
						}
					}

					_activeModeCallback?.Invoke();
				}
				else
				{
					Console.WriteLine($"UserUnlocksRequest failed in ResponseCallback with {res}");
				}

				_lib.rc_api_destroy_fetch_user_unlocks_response(ref resp);
			}

			public override void DoRequest()
			{
				var apiParamsResult = _lib.rc_api_init_fetch_user_unlocks_request(out var api_req, ref _apiParams);
				InternalDoRequest(apiParamsResult, ref api_req);
			}

			public UserUnlocksRequest(string username, string api_token, int game_id, bool hardcore,
				IReadOnlyDictionary<int, Cheevo> cheevos, Action activeModeCallback = null)
			{
				_apiParams = new(username, api_token, game_id, hardcore);
				_cheevos = cheevos;
				_activeModeCallback = activeModeCallback;
			}
		}

		private RCheevoHttpRequest _activeModeUnlocksRequest, _inactiveModeUnlocksRequest;

		private sealed class GameDataRequest : RCheevoHttpRequest
		{
			private LibRCheevos.rc_api_fetch_game_data_request_t _apiParams;
			private readonly Func<bool> _allowUnofficialCheevos;

			public GameData GameData { get; private set; }

			protected override void ResponseCallback(byte[] serv_resp)
			{
				var res = _lib.rc_api_process_fetch_game_data_response(out var resp, serv_resp);
				if (res == LibRCheevos.rc_error_t.RC_OK)
				{
					GameData = new(in resp, _allowUnofficialCheevos);
				}
				else
				{
					Console.WriteLine($"GameDataRequest failed in ResponseCallback with {res}");
				}

				_lib.rc_api_destroy_fetch_game_data_response(ref resp);
			}

			public override void DoRequest()
			{
				GameData = new();
				var apiParamsResult = _lib.rc_api_init_fetch_game_data_request(out var api_req, ref _apiParams);
				InternalDoRequest(apiParamsResult, ref api_req);
			}

			public GameDataRequest(string username, string api_token, int game_id, Func<bool> allowUnofficialCheevos)
			{
				_apiParams = new(username, api_token, game_id);
				_allowUnofficialCheevos = allowUnofficialCheevos;
			}
		}

		private sealed class ImageRequest : RCheevoHttpRequest
		{
			private LibRCheevos.rc_api_fetch_image_request_t _apiParams;

			public Bitmap Image { get; private set; }

			public override bool ShouldRetry => false;

			protected override void ResponseCallback(byte[] serv_resp)
			{
				try
				{
					var image = new Bitmap(new MemoryStream(serv_resp));
					Image = image;
				}
				catch
				{
					Image = null;
				}
			}

			public override void DoRequest()
			{
				Image = null;

				if (_apiParams.image_name is null)
				{
					return;
				}

				var apiParamsResult = _lib.rc_api_init_fetch_image_request(out var api_req, ref _apiParams);
				InternalDoRequest(apiParamsResult, ref api_req);
			}

			public ImageRequest(string image_name, LibRCheevos.rc_api_image_type_t image_type)
			{
				_apiParams = new(image_name, image_type);
			}
		}

		public class GameData
		{
			public int GameID { get; }
			public ConsoleID ConsoleID { get; }
			public string Title { get; }
			private string ImageName { get; }
			public Bitmap GameBadge => _gameBadgeImageRequest?.Image;
			public string RichPresenseScript { get; }

			private ImageRequest _gameBadgeImageRequest;

			private readonly IReadOnlyDictionary<int, Cheevo> _cheevos;
			private readonly IReadOnlyDictionary<int, LBoard> _lboards;

			public IEnumerable<Cheevo> CheevoEnumerable => _cheevos.Values;
			public IEnumerable<LBoard> LBoardEnumerable => _lboards.Values;

			public Cheevo GetCheevoById(int i) => _cheevos[i];
			public LBoard GetLboardById(int i) => _lboards[i];

			public UserUnlocksRequest InitUnlocks(string username, string api_token, bool hardcore, Action callback = null)
			{
				return new(username, api_token, GameID, hardcore, _cheevos, callback);
			}

			public IEnumerable<RCheevoHttpRequest> LoadImages()
			{
				var requests = new List<RCheevoHttpRequest>(1 + (_cheevos?.Count ?? 0) * 2);

				_gameBadgeImageRequest = new(ImageName, LibRCheevos.rc_api_image_type_t.RC_IMAGE_TYPE_GAME);
				requests.Add(_gameBadgeImageRequest);

				if (_cheevos is null) return requests;

				foreach (var cheevo in _cheevos.Values)
				{
					cheevo.LoadImages(requests);
				}

				return requests;
			}

			public int TotalCheevoPoints(bool hardcore)
				=> _cheevos?.Values.Sum(c => c.IsEnabled && !c.Invalid && c.IsUnlocked(hardcore) ? c.Points : 0) ?? 0;

			public unsafe GameData(in LibRCheevos.rc_api_fetch_game_data_response_t resp, Func<bool> allowUnofficialCheevos)
			{
				GameID = resp.id;
				ConsoleID = resp.console_id;
				Title = resp.Title;
				ImageName = resp.ImageName;
				RichPresenseScript = resp.RichPresenceScript;

				var cheevos = new Dictionary<int, Cheevo>();
				var cptr = (LibRCheevos.rc_api_achievement_definition_t*)resp.achievements;
				for (var i = 0; i < resp.num_achievements; i++)
				{
					cheevos.Add(cptr![i].id, new(in cptr[i], allowUnofficialCheevos));
				}

				_cheevos = cheevos;

				var lboards = new Dictionary<int, LBoard>();
				var lptr = (LibRCheevos.rc_api_leaderboard_definition_t*)resp.leaderboards;
				for (var i = 0; i < resp.num_leaderboards; i++)
				{
					lboards.Add(lptr![i].id, new(in lptr[i]));
				}

				_lboards = lboards;
			}

			public GameData(GameData gameData, Func<bool> allowUnofficialCheevos)
			{
				GameID = gameData.GameID;
				ConsoleID = gameData.ConsoleID;
				Title = gameData.Title;
				ImageName = gameData.ImageName;
				RichPresenseScript = gameData.RichPresenseScript;

				_cheevos = gameData.CheevoEnumerable.ToDictionary<Cheevo, int, Cheevo>(cheevo => cheevo.ID, cheevo => new(in cheevo, allowUnofficialCheevos));
				_lboards = gameData.LBoardEnumerable.ToDictionary<LBoard, int, LBoard>(lboard => lboard.ID, lboard => new(in lboard));
			}

			public GameData()
			{
				GameID = 0;
			}
		}

		private sealed class ResolveHashRequest : RCheevoHttpRequest
		{
			private LibRCheevos.rc_api_resolve_hash_request_t _apiParams;
			public int GameID { get; private set; }

			// eh? not sure I want this retried, given the blocking behavior
			public override bool ShouldRetry => false;

			protected override void ResponseCallback(byte[] serv_resp)
			{
				var res = _lib.rc_api_process_resolve_hash_response(out var resp, serv_resp);
				if (res == LibRCheevos.rc_error_t.RC_OK)
				{
					GameID = resp.game_id;
				}
				else
				{
					Console.WriteLine($"ResolveHashRequest failed in ResponseCallback with {res}");
				}

				_lib.rc_api_destroy_resolve_hash_response(ref resp);
			}

			public override void DoRequest()
			{
				GameID = 0;
				var apiParamsResult = _lib.rc_api_init_resolve_hash_request(out var api_req, ref _apiParams);
				InternalDoRequest(apiParamsResult, ref api_req);
			}

			public ResolveHashRequest(string hash)
			{
				_apiParams = new(null, null, hash);
			}
		}

		private int SendHash(string hash)
		{
			var resolveHashRequest = new ResolveHashRequest(hash);
			_inactiveHttpRequests.Push(resolveHashRequest);
			resolveHashRequest.Wait(); // currently, this is done synchronously
			return resolveHashRequest.GameID;
		}

		protected override int IdentifyHash(string hash)
		{
			_gameHash ??= hash;
			return _cachedGameIds.GetValueOrPut(hash, SendHash);
		}

		protected override int IdentifyRom(byte[] rom)
		{
			var hash = new byte[33];
			if (_lib.rc_hash_generate_from_buffer(hash, _consoleId, rom, rom.Length))
			{
				return IdentifyHash(Encoding.ASCII.GetString(hash, 0, 32));
			}

			_gameHash ??= "EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE";
			return 0;
		}

		private void InitGameData()
		{
			_activeModeUnlocksRequest = _gameData.InitUnlocks(Username, ApiToken, HardcoreMode, () =>
			{
				foreach (var cheevo in _gameData.CheevoEnumerable)
				{
					if (cheevo.IsEnabled && !cheevo.IsUnlocked(HardcoreMode))
					{
						_lib.rc_runtime_activate_achievement(ref _runtime, cheevo.ID, cheevo.Definition, IntPtr.Zero, 0);
					}
				}
			});
			_inactiveHttpRequests.Push(_activeModeUnlocksRequest);

			_inactiveModeUnlocksRequest = _gameData.InitUnlocks(Username, ApiToken, !HardcoreMode);
			_inactiveHttpRequests.Push(_inactiveModeUnlocksRequest);

			var loadImageRequests = _gameData.LoadImages();
			_inactiveHttpRequests.PushRange(loadImageRequests.ToArray());

			foreach (var lboard in _gameData.LBoardEnumerable)
			{
				_lib.rc_runtime_activate_lboard(ref _runtime, lboard.ID, lboard.Definition, IntPtr.Zero, 0);
			}

			if (_gameData.RichPresenseScript is not null)
			{
				_lib.rc_runtime_activate_richpresence(ref _runtime, _gameData.RichPresenseScript, IntPtr.Zero, 0);
			}
		}

		private GameData GetGameData(int id)
		{
			var gameDataRequest = new GameDataRequest(Username, ApiToken, id, () => AllowUnofficialCheevos);
			_inactiveHttpRequests.Push(gameDataRequest);
			gameDataRequest.Wait();
			return gameDataRequest.GameData;
		}
	}
}