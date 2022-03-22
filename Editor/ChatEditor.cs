// This Editor Window Requires 3 things to run!

// 1. A Twitch Account (lowercase)
// 2. oAuth token from the Twitch API or from a site: www.twitchapps.com/tmi
// 3. The name of the Twitch channel you want to join (lowercase)


using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using System.IO;
using System.Threading;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEngine.Events;

namespace Quartzified.Editor.Twitch
{
    public enum ConnectionStatus { Normal, Success, Error };

    public class ChatEditor : EditorWindow
    {
        public class NewMessageEvent : UnityEvent<string, bool> { }
        public class ConnectionStatusEvent : UnityEvent<ConnectionStatus, string> { }

        public NewMessageEvent messageEvent = new NewMessageEvent();
        public ConnectionStatusEvent connectionEvent = new ConnectionStatusEvent();

        TcpClient client;
        NetworkStream stream;

        public string ircAddress = "irc.twitch.tv";
        public int port = 6667;

        public string oAuth;
        public string nick;
        public string channel;

        Queue<string> commandQueue = new Queue<string>();
        List<string> recievedMsgs = new List<string>();

        Thread inThread;
        Thread outThread;
        bool stopThreads = false;

        bool online = false;
        bool connected = false;

        //Collection
        public int maxMessages = 60; // We start deleting UI elements when count > maxMessages
        private LinkedList<string> messages = new LinkedList<string>();
        string message;

        //GUI Things

        Vector2 mainScrollPos;
        Vector2 chatScrollPos;
        GUIStyle chatStyle;


        [MenuItem("Quartzified/Twitch Chat")]
        public static void ShowWindow()
        {
            EditorWindow.GetWindow<ChatEditor>("Twitch Chat").Show();
        }

        private void OnEnable()
        {
            nick = EditorPrefs.GetString("Quartzified_TwitchNickName");
            oAuth = EditorPrefs.GetString("Quartzified_TwitchOAuth");
            channel = EditorPrefs.GetString("Quartzified_TwitchChannel");

            messageEvent.AddListener(OnMessageRecieved);

            chatStyle = new GUIStyle();
            chatStyle.richText = true;
        }

        private void Update()
        {
            lock(recievedMsgs)
                if(recievedMsgs.Count > 0)
                {
                    for (int i = 0; i < recievedMsgs.Count; i++)
                    {
                        messageEvent.Invoke(recievedMsgs[i], false);
                    }
                    recievedMsgs.Clear();
                }

        }

        private void OnDestroy()
        {
            EditorPrefs.SetString("Quartzified_TwitchNickName", nick);
            EditorPrefs.SetString("Quartzified_TwitchOAuth", oAuth);
            EditorPrefs.SetString("Quartzified_TwitchChannel", channel);

            Disconnect();
        }

        public void TryConnect()
        {

            if (string.IsNullOrWhiteSpace(oAuth) || string.IsNullOrWhiteSpace(nick) || string.IsNullOrWhiteSpace(channel))
            {
                ConnectionAlert(ConnectionStatus.Error, "Twitch credentials not fully filled out!");
                return;
            }

            ConnectToChat();

        }

        void ConnectToChat()
        {
            client = new TcpClient(ircAddress, port);
            stream = client.GetStream();

            if (!client.Connected)
            {
                ConnectionAlert(ConnectionStatus.Error, "Failed to connect to the Twitch IRC!");
                return;
            }

            ConnectionAlert(ConnectionStatus.Normal, "Attempting to Connect!..");

            online = true;
            stopThreads = false;

            StreamReader input = new System.IO.StreamReader(stream);
            StreamWriter output = new System.IO.StreamWriter(stream);

            output.WriteLine("PASS " + oAuth);
            output.WriteLine("NICK " + nick.ToLower());
            output.Flush();

            inThread = new Thread(() => IRCInput(input, stream));
            inThread.Start();

            outThread = new Thread(() => IRCOutput(output));
            outThread.Start();
        }

        void IRCInput(TextReader input, NetworkStream stream)
        {
            while(!stopThreads)
            {
                if (!stream.DataAvailable)
                    continue;

                string raw;
                try { raw = input.ReadLine(); }
                catch
                {
                    if (connected)
                    {
                        ConnectionAlert(ConnectionStatus.Error, "Error while reading IRC input.");
                        Disconnect(true);
                    }

                    break;
                }

                if (raw == null)
                    continue;

                string ircString = raw;

                if (raw[0] == '@')
                {
                    int ind = raw.IndexOf(' ');

                    ircString = raw.Substring(ind).TrimStart();
                }

                if (ircString[0] == ':')
                {
                    string type = ircString.Substring(ircString.IndexOf(' ')).TrimStart();
                    type = type.Substring(0, type.IndexOf(' '));

                    switch (type)
                    {
                        case "PRIVMSG": // = Chat message
                            lock (recievedMsgs)
                                recievedMsgs.Add(raw);
                            break;
                        case "001": // = Successful IRC connection
                            SendCommand("JOIN #" + channel);
                            ConnectionAlert(ConnectionStatus.Success, "We have succesfully Connected!.. now trying to join channel...");
                            connected = true;
                            break;
                    }
                }

                if (raw.StartsWith("PING"))
                {
                    Debug.Log("Recieved PING! Returning PONG!");
                    SendCommand(raw.Replace("PING", "PONG"));
                }

            }
        }

        void IRCOutput(TextWriter output)
        {
            System.Diagnostics.Stopwatch stopWatch = new System.Diagnostics.Stopwatch();
            stopWatch.Start();
            while(!stopThreads)
            {
                lock(commandQueue)
                {
                    if(commandQueue.Count > 0)
                    {
                        if(stopWatch.ElapsedMilliseconds > 1750)
                        {
                            if (commandQueue.Peek().StartsWith("PRIVMSG #"))
                                OnMessageRecieved(commandQueue.Peek(), true);

                            output.WriteLine(commandQueue.Peek());
                            output.Flush();

                            commandQueue.Dequeue();

                            stopWatch.Reset();
                            stopWatch.Start();
                        }
                    }
                }
            }
        }

        private void Disconnect(bool reconnect = false)
        {
            if (!connected) return;

            connected = false;
            online = false;

            stopThreads = true;

            if(client != null)
            {
                client.Close();
                stream.Close();
            }

            ConnectionAlert(ConnectionStatus.Normal, "Disconnected from Twitch IRC");

            recievedMsgs.Clear();
            messages.Clear();

            if (reconnect)
                TryConnect();
        }

        public void SendCommand(string cmd)
        {
            lock (commandQueue)
            {
                commandQueue.Enqueue(cmd);
            }
        }

        public void SendMsg(string msg)
        {
            lock (commandQueue)
            {
                commandQueue.Enqueue("PRIVMSG #" + channel + " :" + msg);
            }
        }

        void OnMessageRecieved(string msg, bool unityCall = false)
        {
            int msgIndex = msg.IndexOf("PRIVMSG #");
            string msgString = msg.Substring(msgIndex + channel.Length + 11);

            string user;
            if (!unityCall)
                user = msg.Substring(1, msg.IndexOf('!') - 1);
            else
                user = nick;

            if(messages.Count > maxMessages)
            {
                messages.RemoveFirst();
            }

            user = FirstLetterToUpper(user);
            string curTime = "[" + DateTime.Now.ToString("HH:mm:ss") + "] ";

            messages.AddLast("<color=#ffffff><b>" + curTime +  user + ": </b>" + msgString + "</color>");

            if(!unityCall)
                Repaint();
        }

        public void ConnectionAlert(ConnectionStatus state, string message)
        {
            switch (state)
            {
                case ConnectionStatus.Success:
                    Debug.Log("[Success]: " + message);
                    break;
                case ConnectionStatus.Normal:
                    Debug.Log("[Status]: " + message);
                    break;
                case ConnectionStatus.Error:
                    Debug.LogError("[ERROR]: " + message);
                    break;
            }

            connectionEvent?.Invoke(state, message);
        }

        #region GUI CODE

        private void OnGUI()
        {
            GUILayout.BeginVertical();

            mainScrollPos = GUILayout.BeginScrollView(mainScrollPos, GUILayout.Width(this.position.width), GUILayout.Height(this.position.height - 1));

            if (!connected && !online)
                ConnectWindow();
            else
                ChatWindow();

            GUILayout.EndScrollView();

            GUILayout.EndVertical();
        }

        void ConnectWindow()
        {
            nick = EditorGUILayout.TextField("Twitch Nick Name", nick);
            
            oAuth = EditorGUILayout.PasswordField("oAuth Token", oAuth);

            channel = EditorGUILayout.TextField("Channel Name", channel);

            GUILayout.Space(6);

            maxMessages = EditorGUILayout.IntField("Max Messages Shown", maxMessages);

            GUILayout.Space(12);

            if(GUILayout.Button("Connect to Chat"))
            {
                TryConnect();
            }
        }

        void ChatWindow()
        {
            GUILayout.BeginVertical();

            GUILayout.Space(4);

            chatScrollPos = GUILayout.BeginScrollView(chatScrollPos, GUILayout.Width(this.position.width), GUILayout.Height(this.position.height - 50));

            foreach (string msg in messages) 
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(4);
                GUILayout.Label(msg, chatStyle);
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
            }

            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();

            message = GUILayout.TextField(message);

            if (GUILayout.Button("Send Message", GUILayout.Width(128)))
            {
                if (!string.IsNullOrWhiteSpace(message))
                {
                    SendMsg(message); 
                }


                message = "";
            }

            GUILayout.EndHorizontal();

            if (GUILayout.Button("Disconnect", GUILayout.Width(128)))
            {
                Disconnect();
            }

            GUILayout.EndVertical();
        }

        #endregion


        public string FirstLetterToUpper(string str)
        {
            if (str == null)
                return null;

            if (str.Length > 1)
                return char.ToUpper(str[0]) + str.Substring(1);

            return str.ToUpper();
        }
    }
}