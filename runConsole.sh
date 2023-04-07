#!/bin/sh

wd=$(dirname "$0")

if [ -f "${wd}/logs/luxopus.log" ]; then
	oldLog=$(date +"${wd}/logs/luxopus.log."%Y%m%d%H%M)
	mv "${wd}/logs/luxopus.log" "$oldLog"
fi

# appsettings.json is searched for in the working directory, strangely.
cd "${wd}/Rwb.Luxopus.Console"

/usr/bin/screen -dm -S luxopus -L -Logfile "${wd}/log/luxopus.log dotnet run --launch-profile "Luxopus (linux)" --project "${wd}/Rwb.Luxopus.Console/Rwb.Luxopus.Console.csproj"

screen -ls | grep luxopus

