#include <cstddef>
#include <string_view>

/* This classifier never resolves, opens, or modifies a filesystem path. */
extern "C" int errorhunt_is_windows_path(const char* bytes, std::size_t length)
{
    const std::string_view path(bytes, length);
    if (path.size() < 2)
        return 0;
    const auto first = path[0];
    const auto second = path[1];
    const bool drive = ((first >= 'a' && first <= 'z') || (first >= 'A' && first <= 'Z'))
        && second == ':';
    const bool unc = (first == '/' || first == '\\') && (second == '/' || second == '\\');
    return drive || unc;
}
