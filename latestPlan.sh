#!/bin/sh

# The error against actual of MultivariateLinearRegerression interquartile range
# is -11 to 32
# Therefore the suggested override is -11 to +32 of the plan.

planDir="/home/rwb/Luxopus/plans"
f=$(ls "$planDir" | tail -1)

cat "${planDir}/$f" | jq 

