// AudioTastic VST3 scanner (EQ-06): prints the audio effect classes of one VST3 module as JSON on stdout.
// The player runs it as a separate process per module, so a plug-in that crashes or hangs while loading only takes
// this process down; the player then blacklists the module.
//   at_vst3scan <path to .vst3>      exit code 0 = ok, 2 = not a VST3 module, 1 = usage

#include "at_vst3host.h"

#include <cstdio>
#include <string>
#include <vector>

#ifdef _WIN32
#include <windows.h>
#endif

static int Scan(const std::string& path)
{
    std::vector<char> json(1 << 16);
    auto length = at_vst3_scan(path.c_str(), json.data(), static_cast<int32_t>(json.size()));
    if (length >= static_cast<int32_t>(json.size()))
    {
        json.resize(static_cast<size_t>(length) + 1);
        length = at_vst3_scan(path.c_str(), json.data(), static_cast<int32_t>(json.size()));
    }
    if (length < 0)
    {
        std::fprintf(stderr, "%s\n", json.data());
        return 2;
    }
    std::fwrite(json.data(), 1, static_cast<size_t>(length), stdout);
    std::fputc('\n', stdout);
    return 0;
}

#ifdef _WIN32
int wmain(int argc, wchar_t** argv)
{
    if (argc != 2)
    {
        std::fprintf(stderr, "usage: at_vst3scan <path to .vst3>\n");
        return 1;
    }
    auto size = WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, nullptr, 0, nullptr, nullptr);
    std::string path(static_cast<size_t>(size > 0 ? size - 1 : 0), '\0');
    WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, path.data(), size, nullptr, nullptr);
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX); // no crash dialog: the player sees the exit code
    return Scan(path);
}
#else
int main(int argc, char** argv)
{
    if (argc != 2)
    {
        std::fprintf(stderr, "usage: at_vst3scan <path to .vst3>\n");
        return 1;
    }
    return Scan(argv[1]);
}
#endif
