#!/bin/bash
pkill -9 -f 'MirageDiary.Server.dll' 2>/dev/null
screen -S miragediary -X quit 2>/dev/null
screen -wipe 2>/dev/null
sleep 1

# Launch supervisor with built-in log rotation watchdog
screen -dmS miragediary bash -c '
    cd /home/mlowers/miragediary

    # Periodic background log rotation (every 30 minutes, max 20MB)
    (
        while true; do
            sleep 1800
            if [ -f server.log ]; then
                LOG_SIZE=$(stat -c%s server.log 2>/dev/null || echo 0)
                if [ "$LOG_SIZE" -gt 20971520 ]; then
                    tail -n 10000 server.log > server.log.tmp && cat server.log.tmp > server.log && rm -f server.log.tmp
                fi
            fi
        done
    ) &

    while true; do
        if [ -f server.log ]; then
            LOG_SIZE=$(stat -c%s server.log 2>/dev/null || echo 0)
            if [ "$LOG_SIZE" -gt 20971520 ]; then
                tail -n 10000 server.log > server.log.tmp && cat server.log.tmp > server.log && rm -f server.log.tmp
            fi
        fi
        /home/mlowers/.dotnet/dotnet MirageDiary.Server.dll >> server.log 2>&1
        sleep 2
    done
'
sleep 2
echo 'MirageDiary supervisor launched successfully.'
