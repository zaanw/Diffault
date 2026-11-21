local json = require("cjson")
local fields = { language = true, severity = true, file = true, code = true, message = true }
local limit = 64 * 1024 * 1024

local function read_rules(path)
    local file = assert(io.open(path, "rb"))
    local text = file:read(65537) or ""
    file:close()
    assert(#text <= 65536, "Rules exceed 64 KiB")
    local rules = {}
    for line in text:gmatch("[^\r\n]+") do
        if not line:match("^%s*#") and not line:match("^%s*$") then
            local field, value = line:match("^(%a+)\t(.+)$")
            assert(fields[field], "Expected field<TAB>literal in rules")
            rules[#rules + 1] = { field = field, value = value }
        end
    end
    return rules
end

local function filter(rows, rules)
    assert(type(rows) == "table", "Expected a diagnostic array")
    local result = {}
    for _, row in ipairs(rows) do
        assert(type(row) == "table", "Expected a diagnostic object")
        local keep = true
        for _, rule in ipairs(rules) do
            local value = row[rule.field]
            if type(value) == "string" and value:find(rule.value, 1, true) then
                keep = false
                break
            end
        end
        if keep then result[#result + 1] = json.encode(row) end
    end
    return "[" .. table.concat(result, ",") .. "]"
end

assert(#arg == 1, "Usage: lua adapters/filter.lua RULES < diagnostics.json")
local rules = read_rules(arg[1])
local input = io.read(limit + 1) or ""
assert(#input <= limit, "Input exceeds 64 MiB")
assert(input:match("^%s*%["), "Expected a diagnostic array")
io.write(filter(json.decode(input), rules), "\n")
