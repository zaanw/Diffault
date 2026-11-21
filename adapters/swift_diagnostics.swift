import Foundation

struct Diagnostic: Encodable {
    let severity: String
    let file: String?
    let line: Int?
    let column: Int?
    let message: String
    let original: String

    enum CodingKeys: String, CodingKey {
        case language, severity, file, line, column, code, message, original, count
    }

    func encode(to encoder: Encoder) throws {
        var fields = encoder.container(keyedBy: CodingKeys.self)
        try fields.encode("swift", forKey: .language)
        try fields.encode(severity, forKey: .severity)
        try fields.encode(file, forKey: .file)
        try fields.encode(line, forKey: .line)
        try fields.encode(column, forKey: .column)
        try fields.encodeNil(forKey: .code)
        try fields.encode(message, forKey: .message)
        try fields.encode(original, forKey: .original)
        try fields.encode(1, forKey: .count)
    }
}

let located = try NSRegularExpression(pattern: #"^(.+?\.swift):(\d+):(\d+):\s*(error|warning|note):\s*(.+)$"#)
let driver = try NSRegularExpression(pattern: #"^(?:swiftc|<unknown>):\s*(error|warning|note):\s*(.+)$"#)

func parse(_ raw: String) -> Diagnostic? {
    let range = NSRange(raw.startIndex..., in: raw)
    if let match = located.firstMatch(in: raw, range: range) {
        func field(_ index: Int) -> String {
            String(raw[Range(match.range(at: index), in: raw)!])
        }
        return Diagnostic(severity: field(4), file: field(1), line: Int32(field(2)).map(Int.init),
                          column: Int32(field(3)).map(Int.init), message: field(5), original: raw)
    }
    if let match = driver.firstMatch(in: raw, range: range),
       let severity = Range(match.range(at: 1), in: raw),
       let message = Range(match.range(at: 2), in: raw) {
        return Diagnostic(severity: String(raw[severity]), file: nil, line: nil, column: nil,
                          message: String(raw[message]), original: raw)
    }
    return nil
}

var results: [Diagnostic] = []
while let line = readLine() {
    if let item = parse(line) {
        results.append(item)
    }
}
FileHandle.standardOutput.write(try JSONEncoder().encode(results))
