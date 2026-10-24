// Misura CPU ed energia di un processo con proc_pid_rusage (RUSAGE_INFO_V6): gli stessi contatori
// che il kernel tiene per Monitoraggio Attività, leggibili senza privilegi per i processi dell'utente.
// L'energia è una STIMA del modello di potenza del kernel (CPU + GPU), non una lettura al connettore.
//
//   procenergy life <pid>           consumo medio dall'avvio del processo
//   procenergy watch <pid> <sec>    consumo medio in una finestra di <sec> secondi
//   procenergy run <comando…>       consumo totale di un comando fino alla fine (campiona ogni 20 ms)
import Darwin
import Foundation

var tb = mach_timebase_info_data_t()
mach_timebase_info(&tb)
func ns(_ ticks: UInt64) -> Double { Double(ticks) * Double(tb.numer) / Double(tb.denom) }

func usage(_ pid: pid_t) -> rusage_info_v6? {
    var info = rusage_info_v6()
    let r = withUnsafeMutablePointer(to: &info) {
        $0.withMemoryRebound(to: rusage_info_t?.self, capacity: 1) { proc_pid_rusage(pid, RUSAGE_INFO_V6, $0) }
    }
    return r == 0 ? info : nil
}

func cpuSeconds(_ u: rusage_info_v6) -> Double { (ns(u.ri_user_time) + ns(u.ri_system_time)) / 1e9 }

func report(_ title: String, _ a: rusage_info_v6, _ b: rusage_info_v6, wall: Double) {
    let cpu = cpuSeconds(b) - cpuSeconds(a)
    let joules = Double(b.ri_energy_nj &- a.ri_energy_nj) / 1e9
    let wakes = Int(b.ri_pkg_idle_wkups &- a.ri_pkg_idle_wkups)
    let ints = Int(b.ri_interrupt_wkups &- a.ri_interrupt_wkups)
    let insn = Double(b.ri_instructions &- a.ri_instructions) / 1e9
    print(title)
    print(String(format: "  durata            %10.1f s", wall))
    print(String(format: "  tempo CPU         %10.3f s   (%.4f%% di un core)", cpu, cpu / wall * 100))
    print(String(format: "  energia stimata   %10.3f J    (%.1f mWh)", joules, joules / 3.6))
    print(String(format: "  potenza media     %10.1f mW", joules / wall * 1000))
    print(String(format: "  risvegli          %10d  (%.1f/s)   interrupt %d", wakes, Double(wakes) / wall, ints))
    print(String(format: "  istruzioni        %10.2f G", insn))
    print(String(format: "  memoria (footprint) %8.1f MB   picco %.1f MB",
                 Double(b.ri_phys_footprint) / 1_048_576, Double(b.ri_lifetime_max_phys_footprint) / 1_048_576))
}

let args = CommandLine.arguments
guard args.count >= 3 else { print("uso: procenergy life <pid> | watch <pid> <sec> | run <comando…>"); exit(2) }

switch args[1] {
case "life":
    let pid = pid_t(args[2])!
    guard let u = usage(pid) else { print("processo non leggibile"); exit(1) }
    var zero = rusage_info_v6()
    zero.ri_proc_start_abstime = u.ri_proc_start_abstime
    let wall = ns(mach_absolute_time() &- u.ri_proc_start_abstime) / 1e9
    report("pid \(pid), dall'avvio (\(String(format: "%.1f", wall / 3600)) h di orologio, sonno incluso)", zero, u, wall: wall)
case "watch":
    let pid = pid_t(args[2])!
    let secs = Double(args[3]) ?? 60
    guard let a = usage(pid) else { print("processo non leggibile"); exit(1) }
    let t0 = Date()
    Thread.sleep(forTimeInterval: secs)
    guard let b = usage(pid) else { print("processo terminato"); exit(1) }
    report("pid \(pid), finestra di \(Int(secs)) s", a, b, wall: Date().timeIntervalSince(t0))
case "run":
    let p = Process()
    p.executableURL = URL(fileURLWithPath: args[2])
    p.arguments = Array(args.dropFirst(3))
    p.standardOutput = FileHandle.nullDevice
    p.standardError = FileHandle.nullDevice
    let t0 = Date()
    try! p.run()
    var last = rusage_info_v6()
    while p.isRunning {
        if let u = usage(p.processIdentifier) { last = u }
        Thread.sleep(forTimeInterval: 0.02)
    }
    report("\((args[2] as NSString).lastPathComponent) \(args.dropFirst(3).joined(separator: " "))",
           rusage_info_v6(), last, wall: Date().timeIntervalSince(t0))
default:
    print("comando sconosciuto")
}
