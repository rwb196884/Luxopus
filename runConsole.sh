#!/bin/sh

d=$(realpath "$0")
wd=$(dirname "$d")
echo "Luxopus is trying to run at: $wd"

if [ ! -d "${wd}/log" ]; then
	echo "Creating log directory at ${wd}/log"
	mkdir "${wd}/log"
fi

if [ -f "${wd}/log/luxopus.log" ]; then
	oldLog=$(date +"${wd}/log/luxopus.log."%Y%m%d%H%M)
	mv "${wd}/log/luxopus.log" "$oldLog"
fi

# appsettings.json is searched for in the working directory, strangely.
cd "${wd}/Rwb.Luxopus.Console"

/usr/bin/screen -dm -S luxopus -L -Logfile "${wd}/log/luxopus.log" dotnet run --launch-profile "Luxopus (linux)" --project "${wd}/Rwb.Luxopus.Console/Rwb.Luxopus.Console.csproj"

screen -ls | grep luxopus

