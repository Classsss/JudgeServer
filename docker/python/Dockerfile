FROM python:3.9
RUN apt-get update && apt-get install -y time
WORKDIR /app
COPY . .
ENV DIR_NAME=your_dir_name
CMD /usr/bin/time -f "%e\n%M" -o ${DIR_NAME}/stat.txt -- python ${DIR_NAME}/Main.py < ${DIR_NAME}/input.txt > ${DIR_NAME}/result.txt 2> ${DIR_NAME}/runtimeError.txt