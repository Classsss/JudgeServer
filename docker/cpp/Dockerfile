FROM gcc:latest
RUN apt-get update && apt-get install -y time && apt-get install -y gdb
WORKDIR /app
COPY . .
ENV DIR_NAME=your_dir_name
CMD g++ -g ${DIR_NAME}/Main.cpp -o ${DIR_NAME}/Main 2> ${DIR_NAME}/compileError.txt -O2 -w -lm -static -std=gnu++11 && (/usr/bin/time -f "%e\n%M" -o ${DIR_NAME}/stat.txt -- ${DIR_NAME}/Main < ${DIR_NAME}/input.txt > ${DIR_NAME}/result.txt 2> ${DIR_NAME}/runtimeError.txt || gdb --batch --ex "run < ${DIR_NAME}/input.txt" ${DIR_NAME}/Main > ${DIR_NAME}/runtimeError.txt 2>&1)