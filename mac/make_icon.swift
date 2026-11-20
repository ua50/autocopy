// Renders the app icon («A» in a blue rounded square, same as the Windows tray icon) into an .iconset folder.
import Cocoa

let out = CommandLine.arguments[1]
try? FileManager.default.createDirectory(atPath: out, withIntermediateDirectories: true)

func render(_ px: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let s = CGFloat(px)
    let r = NSRect(x: s * 0.1, y: s * 0.1, width: s * 0.8, height: s * 0.8)
    NSColor(red: 0.0, green: 0.47, blue: 0.84, alpha: 1).setFill()
    NSBezierPath(roundedRect: r, xRadius: s * 0.18, yRadius: s * 0.18).fill()
    let a = NSAttributedString(string: "A", attributes: [.font: NSFont.systemFont(ofSize: s * 0.5, weight: .bold),
                                                         .foregroundColor: NSColor.white])
    let sz = a.size()
    a.draw(at: NSPoint(x: (s - sz.width) / 2, y: (s - sz.height) / 2))
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

for base in [16, 32, 128, 256, 512] {
    try! render(base).write(to: URL(fileURLWithPath: "\(out)/icon_\(base)x\(base).png"))
    try! render(base * 2).write(to: URL(fileURLWithPath: "\(out)/icon_\(base)x\(base)@2x.png"))
}
