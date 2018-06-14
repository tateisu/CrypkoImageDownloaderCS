using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using CefSharp;
using CefSharp.OffScreen;
using Newtonsoft.Json;

// usage: CrypkoImageDownloaderCS cardId [-o outfile.jpg]
// default output file name is "{cardId}.jpg".
// use "-o -" to output stdout.

namespace CrypkoImageDownloader
{
    public delegate void ResourceCompleteAction(byte[] data);

    //#####################################################
    // JSON.Net のデシリアライズに使うクラス定義

#pragma warning disable IDE1006 // 命名スタイル
    public class Card
    {
        public long id
        {
            get; set;
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

    //#####################################################
    // コマンドラインオプション

    public class AppOptions
    {
        public string cardId = null;
        public string outputFile = null;
        public string outputFileOriginal = null;
        public string jsonFile = null;
        public string jsonFileOriginal = null;
        public string crawlOwner = null;
        public string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/67.0.3396.79 Safari/537.36";
        public TimeSpan timeout = TimeSpan.FromSeconds( 30.0 );

        public AppOptions(String[] args)
        {
            for (var i = 0; i < args.Length; ++i) {
                System.Diagnostics.Debug.WriteLine( $"{i} {args[ i ]}" );
                var a = args[ i ];
                if (a == "-o") {
                    outputFile = args[ ++i ];
                } else if (a == "-j") {
                    jsonFile = args[ ++i ];
                } else if (a == "--owner") {
                    crawlOwner = args[ ++i ];
                } else if (a == "--user-agent") {
                    userAgent = args[ ++i ];
                } else if (a == "-t") {
                    timeout = TimeSpan.FromSeconds( Double.Parse( args[ ++i ] ) );
                } else {
                    if (cardId != null) {
                        throw new ArgumentException( "multiple card id is not supported." );
                    }
                    cardId = a;
                }
            }
            if (cardId == null && crawlOwner == null)
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

            MakeDir( outputFile );
            MakeDir( jsonFile );
        }

        static void MakeDir(string filePath)
        {
            if (filePath == null || filePath == "" || filePath == "-")
                return;
            var dir = Path.GetDirectoryName( filePath );
            if (dir.Length > 0 && !Directory.Exists( dir )) {
                Directory.CreateDirectory( dir );
            }
        }
    }

    //#####################################################
    // メインプログラム 兼 CefSharpリクエストハンドラ

    public class Program : CefSharp.Handler.DefaultRequestHandler
    {

        public static void Log(string line)
        {
            Console.Error.WriteLine( line );
        }

        private static void Save(string filePath, byte[] data)
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

        public int Run(AppOptions options)
        {
            this.options = options;

            // オーナー指定があればカード一覧を取得する
            if (options.crawlOwner != null) {
                CrawlList( $"category=all&sort=-id&ownerAddr={options.crawlOwner}" );
                if (!EatList()) {
                    Log( "empty list" );
                    return 1;
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
                // クロールとeatList() に時間がかかる場合があるので設定しなおす
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

        List<string> cardIdList = new List<string>();

        // サーチAPIを繰り返し呼び出し、カードIDを収集する
        private void CrawlList(string searchParam)
        {
            // 重複排除のため、一時的にSetに格納する
            SortedSet<String> tmpSet = new SortedSet<String>();

            try {
                using (WebClient client = new WebClient()) {

                    client.Headers.Set( HttpRequestHeader.UserAgent, options.userAgent );
                    client.Headers.Set( HttpRequestHeader.Accept, "application/json, text/plain, */*" );
                    client.Headers.Set( HttpRequestHeader.Referer, "https://crypko.ai/" );
                    client.Headers.Set( HttpRequestHeader.AcceptLanguage, "ja-JP,ja;q=0.9,en-US;q=0.8,en;q=0.7" );
                    // client.Headers.Set( HttpRequestHeader.AcceptEncoding, "gzip, deflate" );

                    for (var page = 1; page < 1000; ++page) {
                        var url = $"https://api.crypko.ai/crypkos/search?{searchParam}" + ( page > 1 ? $"&page={page}" : "" );
                        Log( $"get {url}" );
                        for (var retry = 0; retry < 10; ++retry) {

                            // to avoid rate-limit, sleep before each request
                            Thread.Sleep( 1500 );

                            string jsonString = null;
                            try {
                                // client.DownloadString を使うと文字化けしてJSONパースに失敗する
                                var jsonBytes = client.DownloadData( url );
                                jsonString = System.Text.Encoding.UTF8.GetString( jsonBytes );

                                var searchResult = JsonConvert.DeserializeObject<SearchResult>( jsonString );
                                var crypkos = searchResult.crypkos;
                                Log( $"page={page} count={tmpSet.Count}+{crypkos.Count} / {searchResult.totalMatched}" );
                                if (crypkos.Count == 0) {
                                    Log( "end of list." );
                                    return;
                                }

                                foreach (var card in crypkos) {
                                    var cardId = $"{card.id}";
                                    tmpSet.Add( cardId );
                                }
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
                            }
                        }
                    }
                }
            } finally {
                cardIdList = new List<string>( tmpSet );
            }

        }

        // カードIDのリストの先頭の要素を検証して options を変更する
        // 取得するべきカードがあれば真を返す
        private bool EatList()
        {
            try {
                while (cardIdList.Count > 0) {

                    var cardId = cardIdList[ 0 ];
                    cardIdList.RemoveAt( 0 );

                    options.outputFile = Regex.Replace( options.outputFileOriginal, @"(\d+)(\D*)$", $"{cardId}$2" );
                    if (File.Exists( options.outputFile )) {
                        Log( $"skip {cardId}, already exists {options.outputFile}" );

                        // スキップが多いとブラウザ側がタイムアウトしてしまう問題の回避
                        timeStart = DateTime.Now;
                        continue;
                    }

                    if (options.jsonFileOriginal != null) {
                        options.jsonFile = Regex.Replace( options.jsonFileOriginal, @"(\d+)(\D*)$", $"{cardId}$2" );
                    }

                    options.cardId = cardId;
                    return true;
                }
            } catch (Exception ex) {
                Log( $"{ex}" );
            }
            return false;
        }

        //#################################################################################
        // リソース取得の傍受

        readonly Dictionary<ulong, InterceptResponseFilter> filterMap = new Dictionary<ulong, InterceptResponseFilter>();

        public IResponseFilter MakeFilter(IRequest request, ResourceCompleteAction action)
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
                // フィルタするのはGETの時だけ
                if (request.Method != "GET")
                    return null;

                var uri = request.Url;

                Match m;

                // card detail API
                m = reCrypkoDetailApi.Match( uri );
                if (m.Success) {
                    var cardId = m.Groups[ 1 ].Value;
                    lastCardId = cardId;
                    Log( $"card detail: {cardId} {uri}" );
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
    }

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
}
