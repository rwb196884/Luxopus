# Luxopus

https://github.com/rwb196884/Luxopus

A more obvious name might have been `Octolux`; therefore I didn't choose it.

LUXpower / Octopus control

## Installation

## Configuration

If `/etc/luxopus.config` exists then it will be used.

To run the command line version in linux:
```
#!/bin/sh

if [ -f /home/rwb/luxopus.log ]; then
        mv /home/rwb/luxopus.log /home/rwb/luxopus.log.`date  +%Y%m%d%H%M`
fi

# appsettings.json is searched for in the working directory, strangely.
cd /home/rwb/luxopus/Rwb.Luxopus.Console

/usr/bin/screen -dm -S luxopus -L -Logfile /home/rwb/luxopus.log dotnet run --launch-profile "Luxopus (linux)" --project /home/rwb/luxopus/Rwb.Luxopus.Console/Rwb.Luxopus.Console.csproj
```

### Prerequisites

You require:
* a LUX Power inverter,
* an Octopus Energy account, and
* an installation of InfluxDB and an API key.

### Windows

### Linux

If you're using a Debian based system that uses `systemd` then you're in luck. Otherwise you're on your own.

# What it does

# Time

All times are UTC except:
* LUX