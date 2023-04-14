FROM mcr.microsoft.com/dotnet/sdk:7.0
RUN apt-get update && apt-get install -y time
WORKDIR /app
COPY . .
ENV DIR_NAME=your_dir_name
CMD dotnet build ${DIR_NAME}/Main.csproj -c Release -o ${DIR_NAME} > ${DIR_NAME}/build.log 2>&1 || { cat ${DIR_NAME}/build.log > ${DIR_NAME}/compileError.txt; false; } && /usr/bin/time -f "%e\n%M" -o ${DIR_NAME}/stat.txt -- dotnet ${DIR_NAME}/Main.dll < ${DIR_NAME}/input.txt > ${DIR_NAME}/result.txt 2> ${DIR_NAME}/runtimeError.txt
