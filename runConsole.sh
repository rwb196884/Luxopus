#!/bin/sh

d=$(realpath "$0")
wd=$(dirname "$d")

s=$(screen -ls | grep luxopus | wc -l)
if [ ! $s -eq 0 ]; then
	tc=$(grep TaskCanceled "${wd}/log/luxopus.log" | wc -l)
	if [ $tc -gt 0 ]; then
		echo "TaskCanceled errors: stopping..."
		screen -S luxopus -p 0 -X stuff "^M"
		while [ ! $s -eq 0 ]; do
			sleep 3
			s=$(screen -ls | grep luxopus | wc -l)
		done
		echo "  ...stopped."
	else
		#echo "Already running"
		exit
	fi
fi

echo "Luxopus is starting at: $wd"

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

/usr/bin/screen -dm -S luxopus -L -Logfile "${wd}/log/luxopus.log" /opt/dotnet/dotnet run --launch-profile "Luxopus (linux)" --project "${wd}/Rwb.Luxopus.Console/Rwb.Luxopus.Console.csproj"

screen -ls | grep luxopus

