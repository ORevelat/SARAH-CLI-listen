using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Timers;
using System.Xml.XPath;
using System.Text;

#if MICRO
using System.Speech;
using System.Speech.Recognition;
using System.Speech.AudioFormat;
#endif

#if KINECT
using Microsoft.Speech;
using Microsoft.Speech.Recognition;
using Microsoft.Speech.AudioFormat;
#endif

namespace net.encausse.sarah
{

	class SpeechEngine : IDisposable
	{

		#region Members

		private CultureInfo culture = CultureInfo.CreateSpecificCulture("en-US");

		private static int STATUS_INIT = 0;
		private static int STATUS_START = 1;
		private static int STATUS_STOP = 2;
		private int status = 0;

		private bool LoadAndStarted = false;

		private DateTime loading = DateTime.MinValue;

		private bool IsWorking = false;

		private Timer ctxTimer = null;

		#endregion

		#region Properties

		public SpeechRecognitionEngine Engine { get; protected set; }
		public String Name { get; protected set; }
		public double Confidence { get; protected set; }

		#endregion

		#region Constructor

		public SpeechEngine(String name, String recoId, String language, double confidence)
		{
			Name = name;
			Confidence = confidence;

			var recoInfo = findReconizerInfo(recoId, language, false);
			if (recoInfo == null) { Log("No recognizer found..."); }
			Engine = new SpeechRecognitionEngine(recoInfo);

			var info = Engine.RecognizerInfo;
			Log("Using Recognizer Id: " + info.Id + " Name: " + info.Name + " Culture: " + info.Culture + " Kinect: " + info.AdditionalInfo.ContainsKey("Kinect"));
		}

		#endregion

		#region IDisposable implements

		public void Dispose()
		{
			Log("Dispose engine...");
		}

		#endregion

		#region Public methods

		public void Init()
		{
			Init(2, 0, 0, 0.150, 0.500, false);
		}

		public void Init(int maxAlternate, double initialSilenceTimeout, double babbleTimeout, double endSilenceTimeout, double endSilenceTimeoutAmbiguous, bool adaptation)
		{

			Log("Init recognizer");

			Engine.SpeechRecognized += new EventHandler<SpeechRecognizedEventArgs>(recognizer_SpeechRecognized);
			Engine.RecognizeCompleted += new EventHandler<RecognizeCompletedEventArgs>(recognizer_RecognizeCompleted);
			Engine.AudioStateChanged += new EventHandler<AudioStateChangedEventArgs>(recognizer_AudioStateChanged);
			Engine.SpeechHypothesized += new EventHandler<SpeechHypothesizedEventArgs>(recognizer_SpeechHypothesized);
			Engine.SpeechDetected += new EventHandler<SpeechDetectedEventArgs>(recognizer_SpeechDetected);
			Engine.SpeechRecognitionRejected += new EventHandler<SpeechRecognitionRejectedEventArgs>(recognizer_SpeechRecognitionRejected);

			// http://msdn.microsoft.com/en-us/library/microsoft.speech.recognition.speechrecognitionengine.updaterecognizersetting(v=office.14).aspx
			Engine.UpdateRecognizerSetting("CFGConfidenceRejectionThreshold", (int)(this.Confidence * 100));

			Engine.MaxAlternates = maxAlternate;
			Engine.InitialSilenceTimeout = TimeSpan.FromSeconds(initialSilenceTimeout);
			Engine.BabbleTimeout = TimeSpan.FromSeconds(babbleTimeout);
			Engine.EndSilenceTimeout = TimeSpan.FromSeconds(endSilenceTimeout);
			Engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromSeconds(endSilenceTimeoutAmbiguous);

			// For long recognition sessions (a few hours or more), it may be beneficial to turn off adaptation of the acoustic model. 
			// This will prevent recognition accuracy from degrading over time.
			if (!adaptation)
			{
				Engine.UpdateRecognizerSetting("AdaptationOn", 0);
				Engine.UpdateRecognizerSetting("PersistedBackgroundAdaptation", 0);
			}

			Log("AudioLevel: " + Engine.AudioLevel);
			Log("MaxAlternates: " + Engine.MaxAlternates);
			Log("BabbleTimeout: " + Engine.BabbleTimeout);
			Log("InitialSilenceTimeout: " + Engine.InitialSilenceTimeout);
			Log("EndSilenceTimeout: " + Engine.EndSilenceTimeout);
			Log("EndSilenceTimeoutAmbiguous: " + Engine.EndSilenceTimeoutAmbiguous);

			try { Log("ResourceUsage: " + Engine.QueryRecognizerSetting("ResourceUsage")); } catch (Exception) { }
			try { Log("ResponseSpeed: " + Engine.QueryRecognizerSetting("ResponseSpeed")); } catch (Exception) { }
			try { Log("ComplexResponseSpeed: " + Engine.QueryRecognizerSetting("ComplexResponseSpeed")); } catch (Exception) { }
			try { Log("AdaptationOn: " + Engine.QueryRecognizerSetting("AdaptationOn")); } catch (Exception) { }
			try { Log("PersistedBackgroundAdaptation: " + Engine.QueryRecognizerSetting("PersistedBackgroundAdaptation")); } catch (Exception) { }
		}

		public void SetInputToAudioStream(Stream audioSource, SpeechAudioFormatInfo audioFormat)
		{
			Engine.SetInputToAudioStream(audioSource, audioFormat);
		}

		public void Start()
		{
			try
			{
				Pause(false);
				Log("Start listening...");
				LoadAndStarted = true;
			}
			catch (Exception ex)
			{
				Log("No device found");
				Log("Exception:\n" + ex.StackTrace);
			}
		}

		public void Stop(bool dispose)
		{
			Pause(true);
			if (dispose)
			{
				Engine.Dispose();
			}
			Log("Stop listening...done");
		}

		public void Pause(bool state)
		{
			Log("Pause listening... " + Name + " => " + state + " / " + status);

			if (state && (status == STATUS_START || status == STATUS_INIT))
			{
				Engine.RecognizeAsyncStop();
				status = STATUS_STOP;
			}
			else if (!state && (status == STATUS_STOP || status == STATUS_INIT))
			{
				Engine.RecognizeAsync(RecognizeMode.Multiple);
				status = STATUS_START;
			}
		}

		public void Load(IDictionary<string, SpeechGrammar> cache, bool reload)
		{
			if (reload && Name.Equals("FileSystem")) { return; }
			Log("Loading grammar cache");
			foreach (SpeechGrammar g in cache.Values)
			{
				if (g.LastModified < loading) { continue; }
				Load(g.Name, g.Build());
			}
			loading = DateTime.Now;
		}

		public void Load(String name, Grammar grammar)
		{
			if (grammar == null) { Log("ByPass " + name + " wrong grammar"); return; }
			if ("FileSystem" == Name && LoadAndStarted) { Log("ByPass FileSystem Engine !!!"); return; }

			foreach (Grammar g in Engine.Grammars)
			{
				if (g.Name != name || !g.Loaded) { continue; }
				Log("Try to Unload Grammar to Engine: " + name);
				Engine.UnloadGrammar(g);
				Engine.RequestRecognizerUpdate();
				break;
			}

			Log("Load Grammar to Engine: " + name);
			Engine.LoadGrammar(grammar);
		}

		#endregion

		#region Speech recognized methods

		protected void ProcessSpeechRecognized(RecognitionResult rr)
		{
			// 1. Handle the Working local state
			if (IsWorking)
			{
				Log("REJECTED Speech while working: " + rr.Confidence + " Text: " + rr.Text);
				return;
			}

			// 2. Start
			IsWorking = true;
			var start = DateTime.Now;

			// 3. Handle Results
			try
			{
				ProcessSpeechRecognizedResult(this, rr);
			}
			catch (Exception ex)
			{
				Log("Exception:\n" + ex.StackTrace);
			}

			// 4. End
			IsWorking = false;
			Log("SpeechRecognized: " + (DateTime.Now - start).TotalMilliseconds + "ms Text: " + rr.Text);
		}

		protected void ProcessSpeechRecognizedResult(SpeechEngine engine, RecognitionResult rr)
		{
			StringBuilder builder = new StringBuilder();

			builder.Append("<JSON>");
			using (var stream = new MemoryStream())
			{
				XPathNavigator xnav = rr.ConstructSmlFromSemantics().CreateNavigator();
				var nav = xnav.Select("/SML/action/*");

				string opts = "";
				if (nav.Count > 0)
				{
					opts = "\"options\": {" + Options(nav) + "}, ";
				}

				string txt = "\"text\": \"" + rr.Text.Replace("\"", "\\\"").ToString(culture) + "\", ";
				string conf = "\"confidence\": " + rr.Confidence.ToString(culture).Replace(",", ".") + ", ";

				rr.Audio.WriteToWaveStream(stream);
				stream.Position = 0;
				var base64 = Convert.ToBase64String(stream.GetBuffer());

				string json = "{" + txt + conf + opts + "   \"base64\": \"" + base64 + "\"}";
				builder.Append(json);
			}
			builder.Append("</JSON>");

			Console.Write(builder.ToString());
		}

		#endregion

		#region Private methods

		private void Log(string msg)
		{
			Console.Error.WriteLine(msg);
		}

		private RecognizerInfo findReconizerInfo(String recoId, String language, bool findKinect)
		{
			RecognizerInfo info = null;
			try
			{
				var recognizers = SpeechRecognitionEngine.InstalledRecognizers();

				foreach (var recInfo in recognizers)
				{

					Log("Id: " + recInfo.Id + " Name: " + recInfo.Name + " Culture: " + recInfo.Culture + " Kinect: " + recInfo.AdditionalInfo.ContainsKey("Kinect"));
					if (!language.Equals(recInfo.Culture.Name, StringComparison.OrdinalIgnoreCase))
						continue;
					if (!String.IsNullOrEmpty(recoId) && recoId.Equals(recInfo.Id, StringComparison.OrdinalIgnoreCase))
						continue;
					if (findKinect && recInfo.AdditionalInfo.ContainsKey("Kinect"))
						continue;
					info = recInfo;
				}
			}
			catch (COMException) { }
			return info;
		}

		private void ContextTimeoutStart()
		{
			if (ctxTimer != null)
				return;

			int timeout = 30000;
			Log($"Start context timeout: {timeout} ms");
			ctxTimer = new System.Timers.Timer
			{
				Interval = timeout
			};

			ctxTimer.Elapsed += new System.Timers.ElapsedEventHandler(ContextTimeout_Elapsed);
			ctxTimer.Enabled = true;
			ctxTimer.Start();
		}

		private void ContextTimeoutReset()
		{
			if (ctxTimer == null)
				return;

			Log("Reset timeout");
			ctxTimer.Stop();
			ctxTimer.Start();
		}

		protected void CheckToApplyContext(string key, string value)
		{
			if (!key.Equals("context", StringComparison.InvariantCulture))
				return;

			ContextTimeoutReset();
			LoadContextToEngine(value);
			ContextTimeoutStart();
		}

		protected void LoadContextToEngine(string content)
		{
			bool loadDefault = content.Equals("default", StringComparison.InvariantCulture);

			foreach (Grammar g in Engine.Grammars)
			{
				bool isLazyRule = g.RuleName.IndexOf("lazy") == 0;
				if (loadDefault)
				{
					g.Enabled = !isLazyRule;
					continue;
				}

				g.Enabled = g.Name == content;
			}

			Engine.RequestRecognizerUpdate();
		}

		private String Options(XPathNodeIterator it)
		{
			String json = "";
			while (it.MoveNext())
			{

				if (it.Current.Name == "confidence")
					continue;

				if (it.Current.Name == "uri")
					continue;

				if (it.Current.HasChildren)
				{
					var content = Options(it.Current.SelectChildren(String.Empty, it.Current.NamespaceURI));
					json += "\"" + it.Current.Name + "\" : \"" + content + "\", ";

					CheckToApplyContext(it.Current.Name, content);
				}
			}
			return json.Equals("") ? it.Current.Value : json.Substring(0, json.Length - 2);
		}

		#endregion

		#region Callbacks - Speech engine, Context timer

		private void recognizer_SpeechRecognized(object sender, SpeechRecognizedEventArgs e)
		{
			ProcessSpeechRecognized(e.Result);
		}
		private void recognizer_RecognizeCompleted(object sender, RecognizeCompletedEventArgs e)
		{
			String resultText = e.Result != null ? e.Result.Text : "<null>";
			Log("RecognizeCompleted (" + DateTime.Now.ToString("mm:ss.f") + "): " + resultText);
			Log("BabbleTimeout: " + e.BabbleTimeout + "; InitialSilenceTimeout: " + e.InitialSilenceTimeout + "; Result text: " + resultText);
		}
		private void recognizer_AudioStateChanged(object sender, AudioStateChangedEventArgs e)
		{
			Log("AudioStateChanged (" + DateTime.Now.ToString("mm:ss.f") + "):" + e.AudioState);
		}
		private void recognizer_SpeechHypothesized(object sender, SpeechHypothesizedEventArgs e)
		{
			Log("recognizer_SpeechHypothesized " + e.Result.Text + " => " + e.Result.Confidence);
		}
		private void recognizer_SpeechDetected(object sender, SpeechDetectedEventArgs e)
		{
			Log("recognizer_SpeechDetected");
		}
		private void recognizer_SpeechRecognitionRejected(object sender, SpeechRecognitionRejectedEventArgs e)
		{
			Log("recognizer_SpeechRecognitionRejected");
		}

		private void ContextTimeout_Elapsed(object sender, EventArgs e)
		{
			Log("End context timeout");
			ctxTimer.Stop();
			ctxTimer = null;

			LoadContextToEngine("default");
		}

		#endregion

	}
}






