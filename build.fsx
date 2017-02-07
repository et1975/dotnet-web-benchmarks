// include Fake libs
#r "./packages/FAKE/tools/FakeLib.dll"
#load "./packages/build/FsLab/FsLab.fsx"

open Deedle
open FSharp.Data
open XPlot.GoogleCharts
open XPlot.GoogleCharts.Deedle

#r "./packages/build/Newtonsoft.Json/lib/net45//Newtonsoft.Json.dll"
open Newtonsoft.Json

open Fake
open Fake.EnvironmentHelper
open System
open System.IO
open System.Diagnostics
open System.Net.Sockets
// Directories
let srcDir = "./src"
let reportDir = "./reports"


let stringJoin (separator : string) (strings : string seq) =
    String.Join(separator, strings)


//Proc helpers
let waitForExit ( proc : Process) = proc.WaitForExit()
let startProc fileName args workingDir=
    let proc = 
        ProcessStartInfo(FileName = fileName, Arguments = args, WorkingDirectory = workingDir, UseShellExecute = true) 
        |> Process.Start

    proc 

let execProcAndReturnMessages filename args =
    ExecProcessAndReturnMessages 
                    (fun psi ->
                        psi.FileName <- filename
                        psi.Arguments <-args
                    ) (TimeSpan.FromMinutes(1.))

let getProcessMessages (procResult : ProcessResult) =
    procResult.Messages

//System Helpers
let lsof args = execProcAndReturnMessages "lsof" args
 
let kill procId = 
    execProcAndReturnMessages "kill" (procId |> sprintf "-9 %d") |> ignore

let mono args = execProcAndReturnMessages "mono" args

let dotnetSDKPath = System.Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) </> "dotnetcore" |> FullName

let dotnetExePath =
    dotnetSDKPath </> (if isWindows then "dotnet.exe" else "dotnet")
    |> FullName

let dotnet args = execProcAndReturnMessages dotnetExePath args


let getProcessor () =
    let result =
        if EnvironmentHelper.isMacOS
        then execProcAndReturnMessages "sysctl" "-n machdep.cpu.brand_string" |> getProcessMessages 
        elif EnvironmentHelper.isLinux 
        then execProcAndReturnMessages "cat" "/proc/cpuinfo | grep 'model name' | uniq" |> getProcessMessages 
        else failwith "Not supported OS"
    result |> Seq.head

let getProcessIdByPort port =
    let result = lsof (sprintf "-ti tcp:%d" port)
    result.Messages |> Seq.tryHead |> Option.map int

let waitForPortInUse  port =
    let mutable portInUse = false

    while not portInUse do  
        use client = new TcpClient()
        try
            client.Connect("127.0.0.1",port)
            portInUse <- client.Connected 
            client.Close()
        with e -> 
            client.Close()


let killProcessOnPort port =
    getProcessIdByPort port |> Option.iter kill 



// Filesets
let appReferences  =
    !! "/**/*.csproj"
    ++ "/**/*.fsproj"
    ++ "/**/project.json"

let getProjFile proj  =
    !! (sprintf "src/%s/*.csproj" proj)
    ++ (sprintf "src/%s/*.fsproj" proj)
    ++ (sprintf "src/%s/project.json" proj)
    |> Seq.head


let getExe proj  =
    !! (sprintf "src/**/%s.exe" proj)
    |> Seq.head

// version info
let version = "0.1"  // or retrieve from CI server

// Targets
Target "Clean" (fun _ ->
    (appReferences
    |> Seq.map(fun f -> (System.IO.Path.GetDirectoryName f) @@ "bin")
    |> Seq.toList)
    @
    (appReferences
    |> Seq.map(fun f -> (System.IO.Path.GetDirectoryName f) @@ "obj")
    |> Seq.toList)
    |> CleanDirs
)

let msbuild projFile =
    MSBuildRelease "" "Build" [projFile]
    |> ignore


let msbuildAndRun useLLVM projName =
    projName |> getProjFile |> msbuild 
    let args = [
        (if useLLVM then "--llvm" else "")
        (getExe projName)
    ]

    startProc "mono" (args |> stringJoin " ") ""
    
printf "%s\n" dotnetExePath
let dotnetRestore projFile =    
    let args = "restore"  
    startProc dotnetExePath args (Path.GetDirectoryName projFile)

let dotnetrun projFile =
    let args = sprintf "run --configuration Release --project %s" projFile
    startProc dotnetExePath args (Path.GetDirectoryName projFile)

let dotnetBuildAndRun projName =
    projName |> getProjFile |> dotnetRestore
    // FSharp proj always build https://github.com/dotnet/cli/issues/3996
    // run will build if necessary, so don't need to build before run
    projName |> getProjFile |> dotnetrun



// --------------------------------------------------------------------------------------

type SystemInfo = {
    CPUModel : string
    ProcessorCount : int
    MonoVersion : string seq
    DotnetVersion : string seq
    OperatingSystem : string
}

let getSystemInfo () =
    let machineInfo = EnvironmentHelper.getMachineEnvironment ()
    let monoVersion = mono "--version" |> getProcessMessages 
    let dotnetVersion = dotnet "--version" |> getProcessMessages 
    {
        CPUModel =  getProcessor ()
        ProcessorCount = machineInfo.ProcessorCount
        MonoVersion = monoVersion
        DotnetVersion = dotnetVersion
        OperatingSystem = machineInfo.OperatingSystem
    }

let td inner = 
    sprintf "<td>%s</td>" inner
let tr tds=
    tds 
    |> stringJoin ""
    |> sprintf "<tr>%s</tr>"
let table trs =
    trs 
    |> stringJoin ""
    |> sprintf "<table>%s</table>"

let systemInfoToHtmlTable (sysInfo:SystemInfo) =
    let cpu = sysInfo.CPUModel |> td
    let os = sysInfo.OperatingSystem |> td
    let proc = sysInfo.ProcessorCount |> string |> td
    let monoV = sysInfo.MonoVersion |> Seq.head|> td
    let dotnetV = sysInfo.DotnetVersion|> Seq.head |> td

    table
        [
            tr [td "CPU"; cpu]
            tr [td "Operating System" ;os;]
            tr [td "Processor Count" ;proc;]
            tr [td "Mono Version" ;monoV;]
            tr [td "Dotnet Version" ;dotnetV;]
        ]




//https://github.com/wg/wrk/blob/master/SCRIPTING
//{"bytes":217834904,"duration":30099407,"errors":{"connect":0,"read":182,"status":0,"timeout":0,"write":0},"requests":1785532}
type Error = {
    connect : int
    read : int
    status : int
    timeout : int
    write : int
}

type Summary = {
    bytes : int
    duration : int // in microseconds
    requests : int
    errors : Error
}

type Latency = {
    min : float
    max : float
    mean : float
    stdev : float
}
type ProjectName = string
type Framework =
| Full of ProjectName
| FullLLVM of ProjectName
| Core of ProjectName


let projects =
    [
    //    Full "KatanaPlain"
    //    FullLLVM "KatanaPlain"

        Full "WebApiOnKatana"   
        Full "WebApiOnNowin"   
    //    FullLLVM "WebApiOnKatana"

    //    Full "NancyOnKatana"
    //    FullLLVM "NancyOnKatana"
       
        Full "FreyaOnKatana"
    //    FullLLVM "FreyaOnKatana"


    //    Full "NowinOnMono"
    //    FullLLVM "NowinOnMono"

    //    Full "NancyOnNowin" //Can't seem to handle the load
    //    FullLLVM "NancyOnNowin" //Can't seem to handle the load

        Full "SuaveOnMono"
    //    FullLLVM "SuaveOnMono"

    //    Full "NancyOnSuave"
    //    FullLLVM "NancyOnSuave"

    //    Core "KestrelPlain"
    //    Core "MvcOnKestrel"
    //    Core "NancyOnKestrel"
    //    Core "SuaveOnKestrel"
    //    Core "SuaveOnCoreCLR"
    //     Full "FreyaOnKestrel" // does not work on osx/linux -- curl: (18) transfer closed with outstanding read data remaining
        Full "FreyaOnNowin" 
    ]
let writeToFile filePath str =
    System.IO.File.WriteAllText(filePath, str)
let getHtml (chart : GoogleChart) =
    chart.GetInlineHtml()

let wrk threads connections duration script url=
    let args = sprintf "-t%d -c%d -d%d -s %s %s" threads connections duration script url
    let result = ExecProcessAndReturnMessages 
                    (fun psi ->
                        psi.FileName <- "wrk"
                        psi.Arguments <-args
                    ) (TimeSpan.FromMinutes(5.))
    if result.OK
    then
        let revResults = 
            result.Messages
            |> Seq.rev
            |> Seq.cache
        (
            JsonConvert.DeserializeObject<Summary>(revResults |> Seq.head), 
            (JsonConvert.DeserializeObject<Latency>(revResults |> Seq.skip 1 |> Seq.head))
        )
    else result.Errors |> stringJoin "" |> failwith 
    
let port = 8083

let createPage systemInfo charts =
    sprintf
        """
        <!DOCTYPE html>
        <html>
            <head>
                <meta charset="UTF-8">
                <meta http-equiv="X-UA-Compatible" content="IE=edge" />
                <title>Google Chart</title>
                <script type="text/javascript" src="https://www.google.com/jsapi"></script>
                <script type="text/javascript">
                    google.load('visualization', '1', { 'packages': ['corechart'] });
                </script>
                <style>
                    table {
                        font-family: arial, sans-serif;
                        border-collapse: collapse;
                    }
                    td, th {
                        border: 1px solid #dddddd;
                        text-align: left;
                        padding: 8px;
                    }

                    tr:nth-child(even) {
                        background-color: #dddddd;
                    }
                </style>

            </head>
            <body>
                <h3> SystemInfo </h3>
                    %s
                <br>
                <h3> Results </h3>
                <div>%s</div>
            </body>
        </html>
    """ systemInfo charts

let runBenchmark (projectName, friendlyName, runner) =   
    try

        logfn "---------------> Starting %s <---------------" friendlyName
        killProcessOnPort port 
        use proc = runner projectName

        waitForPortInUse port
        let (summary, latency) = wrk 8 400 30 "./scripts/reportStatsViaJson.lua" "http://127.0.0.1:8083/"
        //Have to kill process by port because dotnet run calls dotnet exec which has a different process id
        killProcessOnPort port 

        logfn "---------------> Finished %s <---------------" friendlyName
        Some (friendlyName, summary, latency)
    with e -> 
        eprintfn "%A" e
        None

let mutable results = null

Target "Benchmark" (fun _ ->
    //Make sure nothing is on this port
    killProcessOnPort port
    results <-
        projects 
        |> Seq.map(fun framework ->
            match framework with
            | Full proj -> (proj, proj, msbuildAndRun false)
            | FullLLVM proj -> (proj, (sprintf "%s-llvm" proj), msbuildAndRun true)
            | Core proj -> (proj, proj, dotnetBuildAndRun)
        )
        |> Seq.choose (runBenchmark)
        |> Seq.toList
        |> Seq.cache
)

let tee f x = f x; x

let createReport (results : seq<ProjectName * Summary * Latency>) =    
    results |> Seq.iter (printfn "%A")
    let ( _,firstResult,_) = results |> Seq.head 
    let duration = (firstResult.duration / 1000000)
    let reportPath = reportDir @@ (sprintf "report-%s.html" (DateTimeOffset.UtcNow.ToString("o")))
    let labels = results |> Seq.map(fun (proj,_,_) -> proj)


    let totalRequests =
        results
        |> Seq.map(fun (_,summary,latency) -> [("Total",summary.requests)])
        |> Chart.Bar
        |> Chart.WithLabels (labels)
        |> Chart.WithTitle (sprintf "Total Requests over %d seconds" duration)
    
    let requestsPerSecond =
        results
        |> Seq.map(fun (_,summary,_) -> [("Req/s", summary.requests/(summary.duration / 1000000))])
        |> Chart.Bar
        |> Chart.WithLabels (labels)
        |> Chart.WithTitle (sprintf "Requests per second over %d seconds" duration)

        
    let meanLatency =
        results
        |> Seq.map(fun (_,_,latency) -> 
            [
                // ("Min", latency.min/1000.); 
                // ("Max", latency.max/1000.); 
                ("Mean", latency.mean/1000.); 
            ])
        |> Chart.Bar
        |> Chart.WithLabels (labels)
        |> Chart.WithTitle (sprintf "Mean latency in milliseconds over %d seconds" duration)
    
    
    [totalRequests;requestsPerSecond;meanLatency]
    |> Seq.map getHtml
    |> stringJoin ""
    |> createPage (systemInfoToHtmlTable(getSystemInfo ()))
    |> writeToFile reportPath
    |> fun _ ->   System.Diagnostics.Process.Start((Path.GetFullPath(reportPath)))  
    |> ignore


Target "GenerateReport" (fun _ ->
    createReport results
)

// Build order
"Clean"
  ==> "Benchmark"
  ==> "GenerateReport"


// start build
RunTargetOrDefault "GenerateReport"
