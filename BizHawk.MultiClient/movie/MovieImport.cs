﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

#pragma warning disable 219

namespace BizHawk.MultiClient
{
	public static class MovieImport
	{
		public static Movie ImportFile(string path, out string errorMsg)
		{
			Movie mov;
			errorMsg = "";
			switch (Path.GetExtension(path).ToUpper())
			{
				case ".FCM":
					mov = ImportFCM(path, out errorMsg);
					break;
				case ".FM2":
					mov = ImportFM2(path, out errorMsg);
					break;
				case ".GMV":
					mov = ImportGMV(path, out errorMsg);
					break;
				case ".MCM":
					mov = ImportMCM(path, out errorMsg);
					break;
				case ".MC2":
					mov = ImportMC2(path, out errorMsg);
					break;
				case ".MMV":
					mov = ImportMMV(path, out errorMsg);
					break;
				case ".SMV":
					// TODO: Decide which SMV parser to use. Perhaps have one parent function that decides.
					mov = new Movie();
					break;
				case ".VBM":
					mov = ImportVBM(path, out errorMsg);
					break;
				default:
					mov = new Movie();
					break;
			}
			mov.WriteMovie();
			return mov;
		}

		public static bool IsValidMovieExtension(string extension)
		{
			string[] extensions = {"FCM", "FM2", "GMV", "MC2", "MMV", "TAS", "VBM"};
			foreach (string ext in extensions)
			{
				if (extension.ToUpper() == "." + ext)
				{
					return true;
				}
			}
			return false;
		}

		private static string ParseHeader(string line, string headerName)
		{
			string str;
			int x = line.LastIndexOf(headerName) + headerName.Length;
			str = line.Substring(x + 1, line.Length - x - 1);
			return str;
		}

		private static bool AddSubtitle(ref Movie m, string subtitleStr)
		{
			if (subtitleStr.Length == 0)
				return false;
			Subtitle s = new Subtitle();
			int x = subtitleStr.IndexOf(' ');
			if (x <= 0)
				return false;
			// Remove the "subtitle" header from the string.
			string sub = subtitleStr.Substring(x + 1, subtitleStr.Length - x - 1);
			x = sub.IndexOf(' ');
			if (x <= 0)
				return false;
			// The frame and message are separated by a space.
			string frame = sub.Substring(0, x);
			string message = sub.Substring(x + 1, sub.Length - x - 1);
			m.Subtitles.AddSubtitle("subtitle " + frame + " 0 0 200 FFFFFFFF " + message);
			return true;
		}

		private static Movie ImportText(string path, out string errorMsg, string emulator)
		{
			errorMsg = "";
			Movie m = new Movie(Path.ChangeExtension(path, ".tas"), MOVIEMODE.PLAY);
			var file = new FileInfo(path);
			using (StreamReader sr = file.OpenText())
			{
				int line = 0;
				string str = "";
				string rerecordStr = "";
				string warning = "";
				while ((str = sr.ReadLine()) != null)
				{
					line++;
					if (str == "")
					{
						continue;
					}
					if (str.Contains("rerecordCount"))
					{
						rerecordStr = ParseHeader(str, "rerecordCount");
						try
						{
							m.SetRerecords(int.Parse(rerecordStr));
						}
						catch
						{
							m.SetRerecords(0);
						}
					}
					else if (str.Contains("StartsFromSavestate"))
					{
						str = ParseHeader(str, "StartsFromSavestate");
						if (str == "1")
						{
							errorMsg = "Movies that begin with a savestate are not supported.";
							return null;
						}
					}
					if (str.StartsWith("emuVersion"))
					{
						m.Header.SetHeaderLine(MovieHeader.EMULATIONVERSION, emulator + " version " + ParseHeader(str, "emuVersion"));
					}
					else if (str.StartsWith("version"))
					{
						m.Header.SetHeaderLine(MovieHeader.MOVIEVERSION, ParseHeader(str, "version"));
					}
					else if (str.StartsWith("romFilename"))
					{
						m.Header.SetHeaderLine(MovieHeader.GAMENAME, ParseHeader(str, "romFilename"));
					}
					else if (str.StartsWith("comment author"))
					{
						m.Header.SetHeaderLine(MovieHeader.AUTHOR, ParseHeader(str, "comment author"));
					}
					else if (str.StartsWith("guid"))
					{
						m.Header.SetHeaderLine(MovieHeader.GUID, ParseHeader(str, "GUID"));
					}
					else if (str.StartsWith("subtitle") || str.StartsWith("sub"))
					{
						AddSubtitle(ref m, str);
					}
					else if (str[0] == '|')
					{
						ArrayList frame = new ArrayList();
						// Split up the sections of the frame.
						string[] sections = str.Split('|');
						string[] buttons = {};
						string console = "";
						switch (emulator)
						{
							case "FCEUX":
								buttons = new string[8] {"Right", "Left", "Down", "Up", "Start", "Select", "B", "A"};
								console = "NES";
								break;
							case "Mednafen/PCEjin":
								buttons = new string[8] {"Up", "Down", "Left", "Right", "B1", "B2", "Run", "Select"};
								console = "PC Engine";
								break;
						}
						SimpleController controllers = new SimpleController();
						controllers.Type = new ControllerDefinition();
						controllers.Type.Name = console + " Controller";
						if (warning == "" && sections[1].Length != 0 && emulator == "FCEUX")
						{
							switch (sections[1][0])
							{
								case '0':
									break;
								case '1':
									controllers["Reset"] = true;
									break;
								case '2':
									if (m.Length() != 0)
									{
										warning = "hard reset";
									}
									break;
								case '4':
									warning = "FDS Insert";
									break;
								case '8':
									warning = "FDS Select";
									break;
								default:
									warning = "unknown";
									break;
							}
							if (warning != "")
							{
								warning = "Unable to import " + warning + " command on line " + line;
							}
						}
						for (int player = 2; player < sections.Length; player++)
						{
							if (sections[player].Length == buttons.Length)
							{
								for (int button = 0; button < buttons.Length; button++)
								{
									controllers["P" + (player - 1).ToString() + " " + buttons[button]] = (
										sections[player][button] != '.'
									);
								}
							}
						}
						MnemonicsGenerator mg = new MnemonicsGenerator();
						mg.SetSource(controllers);
						m.AppendFrame(mg.GetControllersAsMnemonic());
					}
					else
					{
						m.Header.Comments.Add(str);
					}
				}
				if (warning != "")
				{
					Global.RenderPanel.AddMessage(warning);
				}
			}
			return m;
		}

		private static Movie ImportFCM(string path, out string errorMsg)
		{
			errorMsg = "";

			try
			{
				Movie m = new Movie(Path.ChangeExtension(path, ".tas"), MOVIEMODE.PLAY);
				FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read);
				BinaryReader r = new BinaryReader(fs);

				byte[] signatureBytes = new byte[4];
				for (int x = 0; x < 4; x++)
					signatureBytes[x] = r.ReadByte();
				string signature = System.Text.Encoding.UTF8.GetString(signatureBytes);
				if (signature.Substring(0, 3) != "FCM")
				{
					errorMsg = "This is not a valid FCM file!";
					return null;
				}

				UInt32 version = r.ReadUInt32();
				m.Header.SetHeaderLine(MovieHeader.MovieVersion, "FCEU movie version " + version.ToString() + " (.fcm)");

				byte[] flags = new byte[4];
				for (int x = 0; x < 4; x++)
					flags[x] = r.ReadByte();

				UInt32 frameCount = r.ReadUInt32();

				m.SetRerecords((int)r.ReadUInt32());

				UInt32 movieDataSize = r.ReadUInt32();
				UInt32 savestateOffset = r.ReadUInt32();
				UInt32 firstFrameOffset = r.ReadUInt32();

				byte[] romCheckSum = r.ReadBytes(16);
				//TODO: ROM checksum movie header line (MD5)

				UInt32 EmuVersion = r.ReadUInt32();
				m.Header.SetHeaderLine(MovieHeader.EMULATIONVERSION, "FCEU " + EmuVersion.ToString());

				List<byte> romBytes = new List<byte>();
				while (true)
				{
					if (r.PeekChar() == 0)
						break;
					else
						romBytes.Add(r.ReadByte());
				}
				string rom = System.Text.Encoding.UTF8.GetString(romBytes.ToArray());
				m.Header.SetHeaderLine(MovieHeader.GAMENAME, rom);

				r.ReadByte(); //Advance past null byte

				List<byte> authorBytes = new List<byte>();
				while (true)
				{
					if (r.PeekChar() == 0)
						break;
					else
						authorBytes.Add(r.ReadByte());
				}
				string author = System.Text.Encoding.UTF8.GetString(authorBytes.ToArray());
				m.Header.SetHeaderLine(MovieHeader.AUTHOR, author);

				r.ReadByte(); //Advance past null byte

				bool movieSyncHackOn = true;
				if ((int)(flags[0] & 16) > 0)
					movieSyncHackOn = false;

				bool pal = false;
				if ((int)(flags[0] & 4) > 0)
					pal = true;

				m.Header.SetHeaderLine("SyncHack", movieSyncHackOn.ToString());
				m.Header.SetHeaderLine("PAL", pal.ToString());

				//Power on vs reset
				if ((int)(flags[0] & 8) > 0)
				{ } //Power-on = default
				else if ((int)(flags[0] & 2) > 0)
				{ } //we don't support start from reset, do some kind of notification here
				else
				{ } //this movie starts from savestate, freak out here

				//Advance to first byte of input data
				//byte[] throwaway = new byte[firstFrameOffset];
				//r.Read(throwaway, 0, (int)firstFrameOffset);
				r.BaseStream.Position = firstFrameOffset;
				//moviedatasize stuff

				//read frame data
				//TODO: special commands like fds disk switch, etc, and power/reset

				//TODO: use stringbuilder class for speed
				//string ButtonLookup = "RLDUSsBARLDUSsBARLDUSsBARLDUSsBA"; //TODO: This assumes input data is the same in fcm as bizhawk, which it isn't
				string frame = "|0|"; //TODO: read reset command rather than hard code it off
				for (int x = 0; x < frameCount; x++)
				{
					byte joy = r.ReadByte();

					//Read each byte of controller one data

					frame += "|";

					r.ReadBytes(3); //Lose remaining controllers for now
					m.AppendFrame(frame);
				}

				//set 4 score flag if necessary
				r.Close();
				return m;
			}
			catch
			{
				errorMsg = "Error opening file.";
				return null;
			}
		}

		private static Movie ImportFM2(string path, out string errorMsg)
		{
			errorMsg = "";
			Movie m = ImportText(path, out errorMsg, "FCEUX");
			m.Header.SetHeaderLine(MovieHeader.PLATFORM, "NES");
			// TODO: Mnemonic switches.
			// FM2 file format: http://www.fceux.com/web/FM2.html
			return m;
		}

		private static Movie ImportGMV(string path, out string errorMsg)
		{
			errorMsg = "";
			Movie m = new Movie(Path.ChangeExtension(path, ".tas"), MOVIEMODE.PLAY);

			return m;
		}

		private static Movie ImportMCM(string path, out string errorMsg)
		{
			errorMsg = "";
			Movie m = new Movie(Path.ChangeExtension(path, ".tas"), MOVIEMODE.PLAY);
			return m;
		}

		private static Movie ImportMC2(string path, out string errorMsg)
		{
			errorMsg = "";
			Movie m = ImportText(path, out errorMsg, "Mednafen/PCEjin");
			m.Header.SetHeaderLine(MovieHeader.PLATFORM, "MC2");
			// TODO: PCECD equivalent.
			// MC2 file format: http://code.google.com/p/pcejin/wiki/MC2
			return m;
		}

		private static Movie ImportMMV(string path, out string errorMsg)
		{
			errorMsg = "";
			Movie m = new Movie(Path.ChangeExtension(path, ".tas"), MOVIEMODE.PLAY);

			FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read);
			BinaryReader r = new BinaryReader(fs);

			byte[] signatureBytes = new byte[4];
			for (int x = 0; x < 4; x++)
				signatureBytes[x] = r.ReadByte();
			string signature = System.Text.Encoding.UTF8.GetString(signatureBytes);
			if (signature != "MMV\0")
			{
				errorMsg = "This is not a valid MMV file.";
				return null;
			}

			UInt32 version = r.ReadUInt32();
			m.Header.SetHeaderLine(MovieHeader.MOVIEVERSION, "Dega version " + version.ToString());

			UInt32 framecount = r.ReadUInt32();

			m.SetRerecords((int)r.ReadUInt32());

			UInt32 IsFromReset = r.ReadUInt32();
			if (IsFromReset == 0)
			{
				errorMsg = "Movies that begin with a savestate are not supported.";
				return null;
			}

			UInt32 stateOffset = r.ReadUInt32();
			UInt32 inputDataOffset = r.ReadUInt32();
			UInt32 inputPacketSize = r.ReadUInt32();

			byte[] authorBytes = new byte[64];
			for (int x = 0; x < 64; x++)
				authorBytes[x] = r.ReadByte();

			string author = System.Text.Encoding.UTF8.GetString(authorBytes);
			//TODO: remove null characters
			m.Header.SetHeaderLine(MovieHeader.AUTHOR, author);

			//4-byte little endian flags
			byte flags = r.ReadByte();

			bool pal;
			if ((int)(flags & 2) > 0)
				pal = true;
			else
				pal = false;
			m.Header.SetHeaderLine("PAL", pal.ToString());

			bool japan;
			if ((int)(flags & 4) > 0)
				japan = true;
			else
				japan = false;
			m.Header.SetHeaderLine("Japan", japan.ToString());

			bool gamegear;
			if ((int)(flags & 8) > 0)
			{
				gamegear = true;
				m.Header.SetHeaderLine(MovieHeader.PLATFORM, "GG");
			}
			else
			{
				gamegear = false;
				m.Header.SetHeaderLine(MovieHeader.PLATFORM, "SMS");
			}

			r.ReadBytes(3); //Unused flags

			byte[] romnameBytes = new byte[128];
			for (int x = 0; x < 128; x++)
				romnameBytes[x] = r.ReadByte();
			string romname = System.Text.Encoding.UTF8.GetString(romnameBytes.ToArray());
			//TODO: remove null characters
			m.Header.SetHeaderLine(MovieHeader.GAMENAME, romname);

			byte[] MD5Bytes = new byte[16];
			for (int x = 0; x < 16; x++)
				MD5Bytes[x] = r.ReadByte();
			string MD5 = System.Text.Encoding.UTF8.GetString(MD5Bytes.ToArray());
			//TODO: format correctly
			m.Header.SetHeaderLine("MD5", MD5);

			for (int x = 0; x < (framecount); x++)
			{
				byte tmp;
				SimpleController controllers = new SimpleController();
				controllers.Type = new ControllerDefinition();
				controllers.Type.Name = "SMS Controller";
				for (int player = 1; player <= 2; player++)
				{
					tmp = r.ReadByte();
					controllers["P" + player + " Up"] = ((int)(tmp & 1) > 0);
					controllers["P" + player + " Down"] = ((int)(tmp & 2) > 0);
					controllers["P" + player + " Left"] = ((int)(tmp & 4) > 0);
					controllers["P" + player + " Right"] = ((int)(tmp & 8) > 0);
					controllers["P" + player + " B1"] = ((int)(tmp & 16) > 0);
					controllers["P" + player + " B2"] = ((int)(tmp & 32) > 0);
					if (player == 1)
					{
						controllers["Pause"] = (((int)(tmp & 64) > 0 && (!gamegear)) || ((int)(tmp & 128) > 0 && gamegear));
					}
				}
				MnemonicsGenerator mg = new MnemonicsGenerator();
				mg.SetSource(controllers);
				m.AppendFrame(mg.GetControllersAsMnemonic());
			}
			return m;
		}

		private static Movie ImportSMV(string path, out string errorMSG)
		{
			errorMSG = "";
			FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read);
			BinaryReader r = new BinaryReader(fs);

			byte[] signatureBytes = new byte[4];
			for (int x = 0; x < 4; x++)
				signatureBytes[x] = r.ReadByte();
			string signature = System.Text.Encoding.UTF8.GetString(signatureBytes);
			if (signature.Substring(0, 3) != "SMV")
			{
				errorMSG = "This is not a valid SMV file.";
				return null;
			}

			UInt32 version = r.ReadUInt32();

			switch (version)
			{
				case 1:
					return ImportSMV143(r, path);
				case 4:
					return ImportSMV151(r, path);
				case 5:
					return ImportSMV152(r, path);
				default:
				{
					errorMSG = "SMV version not recognized, 143, 151, and 152 are currently supported";
					return null; 
				}
			}
		}

		private static Movie ImportSMV152(BinaryReader r, string path)
		{
			Movie m = new Movie(Path.ChangeExtension(path, ".tas"), MOVIEMODE.PLAY);

			UInt32 GUID = r.ReadUInt32();

			return m;
		}

		private static Movie ImportSMV151(BinaryReader r, string path)
		{
			Movie m = new Movie(Path.ChangeExtension(path, ".tas"), MOVIEMODE.PLAY);

			return m;
		}

		private static Movie ImportSMV143(BinaryReader r, string path)
		{
			Movie m = new Movie(Path.ChangeExtension(path, ".tas"), MOVIEMODE.PLAY);

			UInt32 GUID = r.ReadUInt32();
			m.Header.SetHeaderLine(MovieHeader.GUID, GUID.ToString()); //TODO: format to hex string
			m.SetRerecords((int)r.ReadUInt32());

			UInt32 framecount = r.ReadUInt32();
			byte ControllerFlags = r.ReadByte();

			int numControllers;
			if ((int)(ControllerFlags & 16) > 0)
				numControllers = 5;
			else if ((int)(ControllerFlags & 8) > 0)
				numControllers = 4;
			else if ((int)(ControllerFlags & 4) > 0)
				numControllers = 3;
			else if ((int)(ControllerFlags & 2) > 0)
				numControllers = 2;
			else
				numControllers = 1;

			byte MovieFlags = r.ReadByte();

			if ((int)(MovieFlags & 1) == 0)
				return null; //TODO: Savestate movies not supported error

			if ((int)(MovieFlags & 2) > 0)
			{
				m.Header.SetHeaderLine("PAL", "True");
			}

			byte SyncOptions = r.ReadByte();
			byte SyncOptions2 = r.ReadByte();
			//TODO: these

			UInt32 SavestateOffset = r.ReadUInt32();
			UInt32 FrameDataOffset = r.ReadUInt32();

			//TODO: get extra rom info

			r.BaseStream.Position = FrameDataOffset;
			for (int x = 0; x < framecount; x++)
			{
				//TODO: FF FF for all controllers = Reset
				//string frame = "|0|";
				for (int y = 0; y < numControllers; y++)
				{
					UInt16 fd = r.ReadUInt16();
				}
			}

			return m;
		}

		private static Movie ImportVBM(string path, out string errorMsg)
		{
			errorMsg = "";
			//Converts vbm to native text based format.
			Movie m = new Movie(Path.ChangeExtension(path, ".tas"), MOVIEMODE.PLAY);

			FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read);
			BinaryReader r = new BinaryReader(fs);

			//0xoffset
			//0x00
			UInt32 signature = r.ReadUInt32();  //always 56 42 4D 1A  (VBM\x1A)
			if (signature != 0x56424D1A)
			{
				errorMsg = "This is not a valid VBM file.";
				return null;
			}

			UInt32 versionno = r.ReadUInt32();  //always 1
			UInt32 uid = r.ReadUInt32();		//time of recording
			m.Header.SetHeaderLine(MovieHeader.GUID, uid.ToString());
			UInt32 framecount = r.ReadUInt32();

			//0x10
			UInt32 rerecordcount = r.ReadUInt32();
			m.SetRerecords((int)rerecordcount);
			m.Header.SetHeaderLine(MovieHeader.RERECORDS, m.Rerecords.ToString());
			Byte moviestartflags = r.ReadByte();

			bool startfromquicksave = false;
			bool startfromsram = false;

			if ((moviestartflags & 0x01) > 0) startfromquicksave = true;
			if ((moviestartflags & 0x02) > 0) startfromsram = true;

			if (startfromquicksave & startfromsram)
			{
				errorMsg = "Movies that begin with a savestate are not supported.";
				return null;
			}

			//0x15
			Byte controllerflags = r.ReadByte();

			int numControllers;                   //number of controllers changes for SGB

			if ((controllerflags & 0x08) > 0) numControllers = 4;
			else if ((controllerflags & 0x04) > 0) numControllers = 3;
			else if ((controllerflags & 0x02) > 0) numControllers = 2;
			else numControllers = 1;

			//0x16
			Byte systemflags = r.ReadByte();    //what system is it?

			bool is_gba = false;
			bool is_gbc = false;
			bool is_sgb = false;
			bool is_gb = false;

			if ((systemflags & 0x04) > 0) is_sgb = true;
			if ((systemflags & 0x02) > 0) is_gbc = true;
			if ((systemflags & 0x01) > 0) is_gba = true;
			else is_gb = true;

			if (is_gb & is_gbc & is_gba & is_sgb)
			{
				errorMsg = "Not a valid VBM platform type.";
				return null;
			}
			//TODO: set platform in header

			//0x17
			Byte flags = r.ReadByte();  //emulation flags

			//placeholder for reserved bit (set to 0)
			bool echoramfix = false;
			bool gbchdma5fix = false;
			bool lagreduction = false;
			//placeholder for unsupported bit
			bool rtcenable = false;
			bool skipbiosfile = false;
			bool usebiosfile = false;

			if ((flags & 0x40) > 0) echoramfix = true;
			if ((flags & 0x20) > 0) gbchdma5fix = true;
			if ((flags & 0x10) > 0) lagreduction = true;
			if ((flags & 0x08) > 0) 
			{
				errorMsg = "Invalid VBM file";
				return null;
			}
			if ((flags & 0x04) > 0) rtcenable = true;
			if ((flags & 0x02) > 0) skipbiosfile = true;
			if ((flags & 0x01) > 0) usebiosfile = true;

			//0x18
			UInt32 winsavetype = r.ReadUInt32();
			UInt32 winflashsize = r.ReadUInt32();

			//0x20
			UInt32 gbemulatortype = r.ReadUInt32();

			char[] internalgamename = r.ReadChars(0x0C);
			string gamename = new String(internalgamename);
			m.Header.SetHeaderLine(MovieHeader.GAMENAME, gamename);

			//0x30
			Byte minorversion = r.ReadByte();
			Byte internalcrc = r.ReadByte();
			UInt16 internalchacksum = r.ReadUInt16();
			UInt32 unitcode = r.ReadUInt32();
			UInt32 saveoffset = r.ReadUInt32();		//set to 0 if unused
			UInt32 controllerdataoffset = r.ReadUInt32();

			//0x40  start info.
			char[] authorsname = r.ReadChars(0x40);		//vbm specification states these strings 
			string author = new String(authorsname);	//are locale dependant.
			m.Header.SetHeaderLine(MovieHeader.AUTHOR, author);

			//0x80
			char[] moviedescription = r.ReadChars(0x80);

			//0x0100
			//End of VBM header

			//if there is no SRAM or savestate, the controller data should start at 0x0100 by default,
			//but this is not buaranteed

			//TODO: implement start data. There are no specifics on the googlecode page as to how long
			//the SRAM or savestate should be.

			UInt32 framesleft = framecount;

			r.BaseStream.Position = controllerdataoffset;    //advances to controller data.

			int currentoffset = (int)controllerdataoffset;

			for (int i = 1; i <= framecount; i++)
			{
				UInt16 controllerstate = r.ReadUInt16();
				string frame = "|.|"; //TODO: reset goes here
				if ((controllerstate & 0x0010) > 0) frame += "R"; else frame += ".";
				if ((controllerstate & 0x0020) > 0) frame += "L"; else frame += ".";
				if ((controllerstate & 0x0080) > 0) frame += "D"; else frame += ".";
				if ((controllerstate & 0x0040) > 0) frame += "U"; else frame += ".";
				if ((controllerstate & 0x0008) > 0) frame += "S"; else frame += ".";
				if ((controllerstate & 0x0004) > 0) frame += "s"; else frame += ".";
				if ((controllerstate & 0x0002) > 0) frame += "B"; else frame += ".";
				if ((controllerstate & 0x0001) > 0) frame += "A"; else frame += ".";
				frame += "|";

				m.AppendFrame(frame);
			}

			m.WriteMovie();

			//format: |.|RLDUSsBA| according to "GetControllersAsMnemonic()"
			//note: this is GBC or less ONLY, not GBA (no L or R button)
			//we need to change this when we add reset or whatever.
			//VBM file format: http://code.google.com/p/vba-rerecording/wiki/VBM

			return m;
		}
	}
}