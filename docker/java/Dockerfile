FROM eclipse-temurin:17-jdk-jammy

RUN apt-get update && apt-get install -y time

WORKDIR /app

COPY . .

ENV DIR_NAME=your_dir_name

FROM eclipse-temurin:17-jdk-jammy

RUN apt-get update && apt-get install -y time

WORKDIR /app

COPY . .

ENV DIR_NAME=your_dir_name

CMD /usr/bin/time -f "%e\n%M" -o ${DIR_NAME}/stat.txt -- sh -c "javac ${DIR_NAME}/Main.java 2> $DIR_NAME/compileError.txt && java -cp $DIR_NAME Main < $DIR_NAME/input.txt > $DIR_NAME/submitResult.txt 2> ${DIR_NAME}/runtimeError.txt" &&  sh -c "javac ${DIR_NAME}/Correct.java && java -cp $DIR_NAME Correct < $DIR_NAME/input.txt > $DIR_NAME/correctResult.txt"