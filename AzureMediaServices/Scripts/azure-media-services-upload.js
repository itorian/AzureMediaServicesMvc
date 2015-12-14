
var maxRetries = 3;
var blockLength = 10576; //1048576
var numberOfBlocks = 1;
var currentChunk = 1;
var retryAfterSeconds = 3;

$(document).ready(function () {
    $(document).on("click", "#fileUpload", beginUpload);
    $("#progressBar").progressbar(0);
});

var beginUpload = function () {
    var fileControl = document.getElementById("selectFile");
    if (fileControl.files.length > 0) {
        for (var i = 0; i < fileControl.files.length; i++) {
            uploadMetaData(fileControl.files[i], i);
        }
    }
}

var uploadMetaData = function (file, index) {
    var size = file.size;
    numberOfBlocks = Math.ceil(file.size / blockLength);
    var name = file.name;
    currentChunk = 1;

    $.ajax({
        type: "POST",
        async: false,
        url: "/Video/SetMetadata?blocksCount=" + numberOfBlocks + "&fileName=" + name + "&fileSize=" + size,
    }).done(function (state) {
        if (state === true) {
            $("#fileUpload").hide();
            displayStatusMessage("On your mark, get set, go....");
            sendFile(file, blockLength);
        }
    }).fail(function () {
        $("#fileUpload").show()
        displayStatusMessage("Failed to send MetaData");
    });
}

var sendFile = function (file, chunkSize) {
    var start = 0, end = Math.min(chunkSize, file.size), retryCount = 0, sendNextChunk, fileChunk;

    sendNextChunk = function () {
        fileChunk = new FormData();

        if (file.slice) {
            fileChunk.append('Slice', file.slice(start, end));
        }
        else if (file.webkitSlice) {
            fileChunk.append('Slice', file.webkitSlice(start, end));
        }
        else if (file.mozSlice) {
            fileChunk.append('Slice', file.mozSlice(start, end));
        }
        else {
            displayStatusMessage(operationType.UNSUPPORTED_BROWSER);
            return;
        }
        jqxhr = $.ajax({
            async: true,
            url: ('/Video/UploadChunk?id=' + currentChunk),
            data: fileChunk,
            cache: false,
            contentType: false,
            processData: false,
            type: 'POST'
        }).fail(function (request, error) {
            if (error !== 'abort' && retryCount < maxRetries) {
                ++retryCount;
                setTimeout(sendNextChunk, retryAfterSeconds * 1000);
            }

            if (error === 'abort') {
                displayStatusMessage("Aborted");
            }
            else {
                if (retryCount === maxRetries) {
                    displayStatusMessage("Upload timed out.");
                    resetControls();
                    uploader = null;
                }
                else {
                    displayStatusMessage("Resuming Upload.");
                }
            }

            return;
        }).done(function (notice) {
            if (notice.error || notice.isLastBlock) {
                displayStatusMessage(notice.message);
                if (notice.isLastBlock) {
                    encodeFile(notice.assetId);
                    $("#assetId").val(notice.assetId);
                }
                return;
            }
            ++currentChunk;
            start = (currentChunk - 1) * blockLength;
            end = Math.min(currentChunk * blockLength, file.size);
            retryCount = 0;
            updateProgress();
            if (currentChunk <= numberOfBlocks) {
                sendNextChunk();
            }
        });
    }
    sendNextChunk();
}

var encodeFile = function (assetId) {
    var fileName = (($('#selectFile').val()).split('\\').pop()).split(".")[0];
    $.ajax({
        type: "POST",
        async: false,
        url: "/Video/EncodeToAdaptiveBitrateMP4s?assetId=" + assetId + "&fileName=" + fileName,
    }).done(function (state) {
        if (!state.error) {
            displayStatusMessage(state.message);
            getJobState(state.jobId);
        }
    }).fail(function (state) {
        if (state.error) {
            displayStatusMessage(state.message);
        }
    });
}

// Smells buggy code, needs improvement
var getJobState = function (jobId) {
    var url = "/Video/GetEncodingJobStatus";
    $.get(url, { jobId: jobId })
    .done(function (data) {
        displayStatusMessage("Current encoding job state is : " + data);
        if (data != "Error") {
            if (data != "Finished") {
                //setInterval(function () { getJobState(jobId); }, 5000);
                setTimeout(function () { getJobState(jobId); }, 5000);
            }
            if (data == "Finished") {
                displayStatusMessage("Video is available for streaming. Enjoy!");
            }
        }
        else {
            displayStatusMessage("Job status check error");
        }
        
    })
    .fail(function () {
        displayStatusMessage("Job status check error");
    });
}

var displayStatusMessage = function (message) {
    $("#statusMessage").text(message);
    console.log(message);
}

var updateProgress = function () {
    var progress = currentChunk / numberOfBlocks * 100;
    if (progress <= 100) {
        $("#progressBar").progressbar("option", "value", parseInt(progress));
        displayStatusMessage("Uploaded " + parseInt(progress) + "%");
    }
}