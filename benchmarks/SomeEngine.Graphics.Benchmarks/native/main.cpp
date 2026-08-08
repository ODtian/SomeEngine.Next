#include "benchmark_protocol.h"
#include "native_d3d12.h"

#include <Windows.h>

#include <iostream>

using namespace someengine::graphics::benchmark;

int wmain(int argc, wchar_t** argv)
{
    configuration config;
    bool parsed = false;
    try
    {
        config = parse_arguments(argc, argv);
        parsed = true;
        process_run run = run_native_d3d12(config);
        write_process_json(config.output_path, run);
        std::cout << "NativeCpp: "
                  << (run.result == disposition::passed ? "Passed" : "FunctionalOnly")
                  << " - " << run.reason << '\n';
        return run.result == disposition::passed || run.result == disposition::functional_only ? 0 : 3;
    }
    catch (...)
    {
        const std::string reason = exception_message();
        if (parsed)
        {
            try
            {
                process_run failed{
                    disposition::failed,
                    reason,
                    unavailable_environment(config),
                    {}};
                write_process_json(config.output_path, failed);
            }
            catch (...)
            {
            }
        }
        std::cerr << reason << '\n';
        return parsed ? 3 : 2;
    }
}
