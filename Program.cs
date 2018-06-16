using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Brotli;
using CefSharp;
using CefSharp.OffScreen;
using Newtonsoft.Json;

// usage: CrypkoImageDownloaderCS cardId [-o outfile.jpg]
// default output file name is "{cardId}.jpg".
// use "-o -" to output stdout.

namespace CrypkoImageDownloader
{
    //#####################################################
    // コマンドラインオプション

    public class AppOptions
    {
        public string cardId = null;
        public string outputFile = null;
        public string outputFileOriginal = null;
        public string jsonFile = null;
        public string jsonFileOriginal = null;
        public string crawlParams = null;
        public string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/67.0.3396.79 Safari/537.36";
        public Boolean verbose = false;
        public TimeSpan timeout = TimeSpan.FromSeconds( 30.0 );

        public AppOptions(String[] args)
        {
            for (var i = 0; i < args.Length; ++i) {
                // Program.Log( $"{i} {args[ i ]}" );
                var a = args[ i ];
                if (a == "-o") {
                    outputFile = args[ ++i ];
                } else if (a == "-j") {
                    jsonFile = args[ ++i ];
                } else if (a == "--owner") {
                    var address = args[ ++i ];
                    crawlParams = $"ownerAddr={address}";
                } else if (a == "--like-by") {
                    var address = args[ ++i ];
                    crawlParams = $"filters=liked%3A{address}";
                } else if (a == "--user-agent") {
                    userAgent = args[ ++i ];
                } else if (a == "-t") {
                    timeout = TimeSpan.FromSeconds( Double.Parse( args[ ++i ] ) );
                } else if (a == "-v") {
                    verbose = true;
                } else if (a.StartsWith( "-" )) {
                    throw new ArgumentException( $"unknown option {a}." );
                } else {
                    if (cardId != null) {
                        throw new ArgumentException( "multiple card id is not supported." );
                    }
                    cardId = a;
                }
            }
            if (cardId == null && crawlParams == null)
                throw new ArgumentException( "usage: CrypkoImageDownloader cardId [-o outfile]" );

            if (outputFile == null) {
                if (cardId == null) {
                    outputFile = "0.jpg"; // will be replaced in eatList()
                } else {
                    outputFile = $"{cardId}.jpg";
                }
            }

            outputFileOriginal = outputFile;
            jsonFileOriginal = jsonFile;

            Program.MakeDir( outputFile );
            Program.MakeDir( jsonFile );
        }

    }

    //#####################################################
    // メインプログラム 兼 CefSharpリクエストハンドラ

    public class Program : CefSharp.Handler.DefaultRequestHandler
    {

        static int Main(string[] args)
        {
            return new Program().Run( new AppOptions( args ) );
        }

        readonly Object mainLock = new Object();

        AppOptions options;
        long isCompleted = 0;
        long returnCode = 30; // unknown error
        DateTime timeStart = DateTime.MaxValue;
        DateTime nextPageTime = DateTime.MaxValue;
        string nextPageUrl = null;
        string lastCardId = null;
        SHA1 sha1Encoder = new SHA1CryptoServiceProvider();
        WebClient client = new WebClient();
        List<Card> cardList = new List<Card>();
        private int cardCount = 0;
        private int skipCount = 0;

        public int Run(AppOptions options)
        {
            this.options = options;

            // クロール指定があれば、先にカードリストを取得する
            if (options.crawlParams != null) {
                CrawlList( $"category=all&sort=-id&{options.crawlParams}" );
                if (!EatList()) {
                    Log( "There are no need to use browser. exit…" );
                    return 0;
                }
            }

            // Cefの初期化
            var multiThreadedMessageLoop = true;
            Cef.Initialize( new CefSettings() {
                WindowlessRenderingEnabled = true,
                MultiThreadedMessageLoop = multiThreadedMessageLoop,
                ExternalMessagePump = !multiThreadedMessageLoop,
                LogSeverity = LogSeverity.Error,
                UserAgent = options.userAgent
            } );

            // ブラウザの作成
            var browser = new ChromiumWebBrowser( $"https://crypko.ai/#/card/{options.cardId}" ) {
                RequestHandler = this
            };

            try {
                // クロールに時間がかかる場合があるので設定しなおす
                timeStart = DateTime.Now;

                // メインスレッドの待機ループ
                var waitInterval = TimeSpan.FromSeconds( 1.0 );
                while (Interlocked.Read( ref isCompleted ) == 0) {

                    // timeStart,nextPageUrl,nextPageTime は別スレッドから変更される
                    Interlocked.MemoryBarrier();

                    var now = DateTime.Now;

                    // イベントが起きずに一定時刻が経過したら終了する
                    var elapsed = now - timeStart;
                    if (elapsed > options.timeout) {
                        Log( "timeout" );
                        return 20;
                    }

                    // 指定があれば次ページに移動する
                    if (nextPageUrl != null && now >= nextPageTime) {
                        browser.Load( nextPageUrl );
                        nextPageUrl = null;
                        nextPageTime = DateTime.MaxValue;
                        continue;
                    }

                    // 短時間の待機
                    lock (mainLock) {
                        Monitor.Wait( mainLock, waitInterval );
                    }
                }

                return (int)Interlocked.Read( ref returnCode );

            } finally {
                // ブラウザの破棄
                Log( "Dispose browser." );
                browser.Dispose();

                // Clean up Chromium objects.
                // You need to call this in your application otherwise you will get a crash when closing.
                Log( "Shutdown Cef." );
                Cef.Shutdown();
            }
        }

        // メインスレッドの待機ループを起こす
        public void BreakLoop(long code)
        {
            // 画像を読めたなら次の画像のロードを指示する
            if (code == 0 && EatList()) {
                timeStart = DateTime.Now;
                // rate limit 対策。ページのロードを少し遅らせる
                nextPageTime = timeStart + TimeSpan.FromSeconds( 2 );
                nextPageUrl = $"https://crypko.ai/#/card/{options.cardId}";
                lock (mainLock) {
                    Monitor.Pulse( mainLock );
                }
                return;
            }

            // メインスレッドの待機ループを終了させる
            Interlocked.Exchange( ref returnCode, code );
            Interlocked.Exchange( ref isCompleted, 1L );
            lock (mainLock) {
                Monitor.Pulse( mainLock );
            }
        }

        //#################################################################################
        // search APIでカードIDを取得


        // サーチAPIを繰り返し呼び出し、カードIDを収集する
        void CrawlList(string searchParam)
        {
            // 重複排除のため、一時的にSetに格納する
            HashSet<Card> tmpSet = new HashSet<Card>();

            try {
                    client.Headers.Set( HttpRequestHeader.UserAgent, options.userAgent );
                    client.Headers.Set( HttpRequestHeader.Accept, "application/json, text/plain, */*" );
                    client.Headers.Set( HttpRequestHeader.Referer, "https://crypko.ai/" );
                    client.Headers.Set( HttpRequestHeader.AcceptLanguage, "ja-JP,ja;q=0.9,en-US;q=0.8,en;q=0.7" );
                    client.Headers.Set( HttpRequestHeader.AcceptEncoding, "gzip, deflate, br" );

                    for (var page = 1; page < 1000; ++page) {

                        var url = $"https://api.crypko.ai/crypkos/search?{searchParam}" + ( page > 1 ? $"&page={page}" : "" );

                        for (var retry = 0; retry < 10; ++retry) {


                            string jsonString = null;
                            try {

                                // to avoid rate-limit, sleep before each request
                                Thread.Sleep( 1500 );


                                // client.DownloadString を使うと文字化けしてJSONパースに失敗する
                                Log( $"get {url}" );
                                var jsonBytes = DecodeContentEncoding( client.DownloadData( url ) );
                                jsonString = Encoding.UTF8.GetString( jsonBytes );

                                var searchResult = JsonConvert.DeserializeObject<SearchResult>( jsonString );
                                var crypkos = searchResult.crypkos;
                                if (crypkos.Count == 0) {
                                    Log( $"page={page} end of list." );
                                    return;
                                }

                                foreach (var card in crypkos) {
                                    tmpSet.Add( card );
                                }

                                Log( $"page={page} count={tmpSet.Count}/{searchResult.totalMatched} {(int)( ( tmpSet.Count * 100 ) / (float)searchResult.totalMatched )}%" );

                                break; // end of retry
                            } catch (Newtonsoft.Json.JsonReaderException ex) {
                                // JSONパースに失敗した場合はリトライしない
                                Log( $"{ex}" );
                                if (jsonString != null)
                                    Log( jsonString );
                                tmpSet.Clear();
                                return;
                            } catch (Exception ex) {
                                Log( $"{ex}" );
                                if (ex is WebException wex) {
                                    var response = (HttpWebResponse)wex.Response;
                                    if (response != null) {
                                        var code = (int)response.StatusCode;
                                        if (400 <= code && code < 500) {
                                            tmpSet.Clear();
                                            return;
                                        }
                                    }
                                }
                            }
                        }
                    }
            } finally {
                cardList = new List<Card>( tmpSet );
                cardList.Sort();
                cardCount = cardList.Count;
                Log( $"Found {cardCount} cards." );
            }
        }



        // カードIDのリストの先頭の要素を検証して options を変更する
        // 取得するべきカードがあれば真を返す
        bool EatList()
        {
            try {
                while (cardList.Count > 0) {

                    // スキップが多いとブラウザ側がタイムアウトしてしまう問題の回避
                    timeStart = DateTime.Now;

                    var card = cardList[ 0 ];
                    cardList.RemoveAt( 0 );

                    options.outputFile = Regex.Replace( options.outputFileOriginal, @"(\d+)(\D*)$", $"{card.id}$2" );
                    if (File.Exists( options.outputFile )) {
                        ++skipCount;
                        continue;
                    }

                    if (options.jsonFileOriginal != null) {
                        options.jsonFile = Regex.Replace( options.jsonFileOriginal, @"(\d+)(\D*)$", $"{card.id}$2" );
                    }

                    options.cardId = card.id.ToString();

                    if (SimpleDownload( card )) {
                        continue;
                    }

                    return true;
                }

                if (skipCount > 0) {
                    Log( $"NOTICE: {skipCount}/{cardCount} cards are skipped because image files are already exist." );
                }

            } catch (Exception ex) {
                Log( $"{ex}" );
            }
            return false;
        }


        bool SimpleDownload(Card card)
        {
            client.Headers.Set( HttpRequestHeader.UserAgent, options.userAgent );
            client.Headers.Set( HttpRequestHeader.Referer, "https://crypko.ai/" );

            {
                var hash = ToHexString( sha1Encoder.ComputeHash( Encoding.UTF8.GetBytes( $"{card.noise}asdasd3edwasd{card.attrs}" ) ) );
                var url = $"https://img.crypko.ai/daisy/{hash}_lg.jpg";
                if (!DownloadFile( url, options.outputFile ))
                    return false;
            }

            if (options.jsonFile != null) {
                // to avoid rate-limit, sleep before each API request
                Thread.Sleep( 1500 );
                var url = $"https://api.crypko.ai/crypkos/{options.cardId}/detail";
                if (!DownloadFile( url, options.jsonFile ))
                    return false;
            }

            return true;
        }

        bool DownloadFile(string url, string path)
        {
            for (var retry = 0; retry < 10; ++retry) {
                try {
                    Log( $"get {url}" );
                    var data = DecodeContentEncoding( client.DownloadData( url ) );
                    Save( path, data );
                    return true;
                } catch (Exception ex) {
                    Log( $"{ex}" );
                    if (ex is WebException wex) {
                        var response = (HttpWebResponse)wex.Response;
                        if (response != null) {
                            var code = (int)response.StatusCode;
                            if (400 <= code && code < 500)
                                return false;
                        }
                    }
                }
                // to avoid rate-limit, sleep before retry.
                Thread.Sleep( 1500 );
            }
            return false;

        }

        byte[] DecodeContentEncoding(byte[] data)
        {
            var contentType = client.ResponseHeaders.Get( "Content-Type" );
            var contentEncoding = client.ResponseHeaders.Get( "Content-Encoding" );
            if (options.verbose)
                Log( $"contentEncoding={contentEncoding}, contentType={contentType}" );
            if (contentEncoding == "br") {
                data = GetBytesFromStream( new BrotliStream( new MemoryStream( data ), CompressionMode.Decompress ) );
            } else if (contentEncoding == "gzip") {
                data = GetBytesFromStream( new GZipStream( new MemoryStream( data ), CompressionMode.Decompress ) );
            } else if (contentEncoding == "deflate") {
                data = GetBytesFromStream( new DeflateStream( new MemoryStream( data ), CompressionMode.Decompress ) );
            }
            return data;

        }


        //#################################################################################
        // リソース取得の傍受

        readonly Dictionary<ulong, InterceptResponseFilter> filterMap = new Dictionary<ulong, InterceptResponseFilter>();

        IResponseFilter MakeFilter(IRequest request, ResourceCompleteAction action)
        {
            var dataFilter = new InterceptResponseFilter( action );
            filterMap.Add( request.Identifier, dataFilter );
            return dataFilter;
        }

        readonly Regex reCrypkoDetailApi = new Regex( @"https://api.crypko.ai/crypkos/(\d+)/detail" );
        readonly Regex reCrypkoImageUrl = new Regex( @"https://img.crypko.ai/daisy/([A-Za-z0-9]+)_lg\.jpg" );

        public override IResponseFilter GetResourceResponseFilter(IWebBrowser browserControl, IBrowser browser, IFrame frame, IRequest request, IResponse response)
        {
            try {
                // detail API はOPTIONSとGETの2回呼び出される。
                // GETだけ傍受する
                if (request.Method != "GET")
                    return null;

                var uri = request.Url;

                Match m;

                // card detail API
                m = reCrypkoDetailApi.Match( uri );
                if (m.Success) {
                    var cardId = m.Groups[ 1 ].Value;
                    this.lastCardId = cardId;
                    Log( $"card detail: {uri}" );
                    if (options.jsonFile != null) {
                        return MakeFilter( request, (data) => {
                            Save( options.jsonFile, data );
                        } );
                    }
                    return null;
                }

                // image
                m = reCrypkoImageUrl.Match( uri );
                if (m.Success) {
                    var imageId = m.Groups[ 1 ].Value;
                    Log( $"Image URL: {uri}" );
                    if (lastCardId != options.cardId) {
                        Log( $"card ID not match. expected:{options.cardId} actually:{lastCardId}" );
                        BreakLoop( 2 );
                        return null;
                    }
                    return MakeFilter( request, (data) => {
                        Save( options.outputFile, data );
                        BreakLoop( 0 );
                    } );
                }
            } catch (Exception ex) {
                Log( $"GetResourceResponseFilter: catch exception. {ex}" );
                BreakLoop( 10 );
            }
            return null;
        }

        public override void OnResourceLoadComplete(IWebBrowser browserControl, IBrowser browser, IFrame frame, IRequest request, IResponse response, UrlRequestStatus status, long receivedContentLength)
        {
            try {
                if (filterMap.TryGetValue( request.Identifier, out InterceptResponseFilter filter )) {
                    filterMap.Remove( request.Identifier );
                    filter.action( filter.Data );
                }
            } catch (Exception ex) {
                Log( $"OnResourceLoadComplete: catch exception. {ex}" );
                BreakLoop( 11 );
            }
        }

        //###################################################################################
        // Utilities

        public static void Log(string line)
        {
            Console.Error.WriteLine( line );
        }

        public static void Save(string filePath, byte[] data)
        {
            if (filePath == "-") {
                using (Stream stdout = Console.OpenStandardOutput()) {
                    stdout.Write( data, 0, data.Length );
                }
                Log( "Wrote to Stdout" );
            } else {
                File.WriteAllBytes( filePath, data );
                Log( $"Saved: {filePath}" );
            }
        }

        public static byte[] GetBytesFromStream(Stream src, Boolean leaveOpen = false)
        {
            try {
                using (var streamOut = new MemoryStream()) {
                    src.CopyTo( streamOut );
                    streamOut.Seek( 0, System.IO.SeekOrigin.Begin );
                    return streamOut.ToArray();
                }
            } finally {
                if (!leaveOpen) {
                    try {
                        src.Close();
                    } catch (Exception) {

                    }
                }
            }
        }

        public static void MakeDir(string filePath)
        {
            if (filePath == null || filePath == "" || filePath == "-")
                return;
            var dir = Path.GetDirectoryName( filePath );
            if (dir.Length > 0 && !Directory.Exists( dir )) {
                Directory.CreateDirectory( dir );
            }
        }

        public static string ToHexString(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder( bytes.Length * 2 );
            foreach (byte b in bytes) {
                if (b < 16)
                    sb.Append( '0' ); // 二桁になるよう0を追加
                sb.Append( Convert.ToString( b, 16 ) );
            }
            return sb.ToString();
        }

    }

    public delegate void ResourceCompleteAction(byte[] data);

    // リソースの傍受に使うレスポンスフィルタ
    public class InterceptResponseFilter : IResponseFilter
    {
        public ResourceCompleteAction action;

        public InterceptResponseFilter(ResourceCompleteAction action)
        {
            this.action = action;
        }

        private MemoryStream memoryStream = new MemoryStream();

        public byte[] Data
        {
            get {
                return memoryStream.ToArray();
            }
        }

        void IDisposable.Dispose()
        {
            memoryStream.Dispose();
            memoryStream = null;
        }

        bool IResponseFilter.InitFilter()
        {
            return true;
        }

        FilterStatus IResponseFilter.Filter(Stream dataIn, out long dataInRead, Stream dataOut, out long dataOutWritten)
        {
            if (dataIn == null) {
                dataInRead = 0;
                dataOutWritten = 0;
                return FilterStatus.Done;
            }

            dataInRead = dataIn.Length;
            dataOutWritten = Math.Min( dataInRead, dataOut.Length );

            //Important we copy dataIn to dataOut
            dataIn.CopyTo( dataOut );

            //Copy data to stream
            dataIn.Position = 0;
            dataIn.CopyTo( memoryStream );

            return FilterStatus.Done;
        }
    }

    //#####################################################
    // JSON.Net のデシリアライズに使うクラス定義

#pragma warning disable IDE1006 // 命名スタイル
    public class Card : IComparable<Card>
    {
        public long id
        {
            get; set;
        }
        public string attrs
        {
            get; set;
        }
        public string noise
        {
            get; set;
        }
        // 本当はもっと多くの情報があるが、アプリから使う項目だけ定義する

        // コレクション用
        public override Boolean Equals(Object other)
        {
            if (other is Card) {
                id.Equals( ( (Card)other ).id );
            }
            return false;
        }

        public override int GetHashCode()
        {
            return id.GetHashCode();
        }

        public int CompareTo(Card other)
        {
            return id.CompareTo( other.id );
        }
    }

    public class SearchResult
    {
        public long totalMatched
        {
            get; set;
        }
        public IList<Card> crypkos
        {
            get; set;
        }
    }
#pragma warning restore IDE1006 // 命名スタイル

}
