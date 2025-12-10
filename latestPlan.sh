#!/bin/sh

# The error against actual of MultivariateLinearRegerression interquartile range
# is -11 to 32
# Therefore the suggested override is -11 to +32 of the plan.

planDir="/home/rwb/Luxopus/plans"
f=$(ls "$planDir" | tail -1)

if [ -z "$1" ]; then
	cat "${planDir}/$f" | jq 
elif [ "$1" = "t" ]; then
	s=$( cat "${planDir}/$f" | jq  -r '.Plans | map(.Start) | max' )
	if [ $( date -d "$s" +%s ) -lt $( date +%s ) ]; then
		echo 0
		exit 1
	else
		echo 1
	fi
fi

