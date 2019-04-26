import numpy as np
import imutils
import argparse
import cv2
import math
import socket
import struct

maincam = cv2.VideoCapture(0)
UDP_IP_ADDRESS = "127.0.0.1"
udp_reciever = 6800
udp1 = 6789
udp2 = 6790
udp3 = 6791
udpcircleX = 6792
udpcircleY = 6793


RecieverSock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
##RecieverSock.setsockopt(SOL_SOCKET, SO_REUSEADDR, 1)
RecieverSock.bind((UDP_IP_ADDRESS, udp_reciever))
data, address = RecieverSock.recvfrom(1024)
phase = int.from_bytes(data,byteorder='little')
   

while phase == 1:

    
    _, image = maincam.read()

    hsv = cv2.cvtColor(image, cv2.COLOR_BGR2HLS)
    lowh = np.array([0.0, 0.0, 32.10431654676259])
    upph = np.array([5.757575757575761, 212.07070707070707, 255.0])
    mask = cv2.inRange(hsv, lowh, upph)
    edges = cv2.Canny(mask,150,200)
    ret,thresh = cv2.threshold(mask, 40, 255, 0) 
    radius = 24.324324324324326
    ksize = int(6 * round(radius) + 1)
    output = image.copy()
    res2=cv2.GaussianBlur(thresh,(ksize, ksize), round(radius))
    circles = cv2.HoughCircles(res2, cv2.HOUGH_GRADIENT, 1, 200, param1=30, param2=35, minRadius=5, maxRadius=0)

    if circles is not None:
		# convert the (x, y) coordinates and radius of the circles to integers
        circles = np.round(circles[0, :]).astype("int")
		
		# loop over the (x, y) coordinates and radius of the circles
        for (x, y, r) in circles:
            cv2.circle(output, (x, y), r, (0, 255, 0), 4)
            cv2.rectangle(output, (x - 5, y - 5), (x + 5, y + 5), (0, 128, 255), -1)
                #time.sleep(0.5)
            print ("X coordinate:")
            print (x)
            print ("Y coordinate: ")
            print (y)
            print ("Radius is: ")
            print (r)

        
            ballbyte = bytearray(struct.pack("i", int(x)))
            ballbyte += bytearray(struct.pack("i", int(y)))
            bcenterSock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            bcenterSock.sendto(bcenterbyte, (UDP_IP_ADDRESS, udp2))
##        bcenterXbyte = bytearray(struct.pack("i", int(x)))
##        bcenterYbyte = bytearray(struct.pack("i", int(y)))
##        bcenterXSock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
##        bcenterXSock.sendto(bcenterXbyte, (UDP_IP_ADDRESS, udpcircleX))
##        bcenterYSock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
##        bcenterYSock.sendto(bcenterYbyte, (UDP_IP_ADDRESS, udpcircleY))

    cv2.imshow('gray',res2)
    cv2.imshow('frame',output)
    
    RecieverSock.setblocking(0)
    try:
        data, address = RecieverSock.recvfrom(1024)
        phase = int.from_bytes(data,byteorder='little')
    except socket.error:
         phase = 1

    if cv2.waitKey(1) & 0xFF == ord('q'):
        maincam.release()
        cv2.destroyAllWindows()
        break


cv2.destroyAllWindows()

while phase==2:

        _, image = maincam.read()
        hsv = cv2.cvtColor(image, cv2.COLOR_BGR2HLS)
        lowh = np.array([111.69064748201437, 0.0, 171.98741007194246])
        upph = np.array([180.0, 197.045454545454545, 255.0])
        output = image.copy()
        mask = cv2.inRange(hsv, lowh, upph)

        
       
        ret,thresh = cv2.threshold(mask, 40, 255, 0)
        
        im2,contours,hierarchy = cv2.findContours(thresh, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
        
        ta = 100
        if len(contours) != 0:
                c = max(contours, key = cv2.contourArea)
                M = cv2.moments(c)
                a = cv2.contourArea(c)
                if a > ta:                        
                        cv2.drawContours(image, c, -1, 255, 3)
                        cX = int(M["m10"] / M["m00"])
                        cY = int(M["m01"] / M["m00"])
                                                
                        cv2.circle(image, (cX, cY), 7, (255, 255, 255), -1)

                        cv2.putText(image, "center", (cX - 20, cY - 20),
                                       cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 255, 255), 2)
                        
                        rows,cols = image.shape[:2]
                        [vx,vy,x,y] = cv2.fitLine(c, cv2.DIST_L2,0,0.01,0.01)
                        left = int((-x*vy/vx) + y)
                        right = int(((cols-x)*vy/vx)+y)
                        image = cv2.line(image,(cols-1,right),(0,left),(0,255,0),2)

                        if(right > cY):
                                anglefound = math.atan2(right-cY, (cols-1) - cX ) * (180/3.141592653589793)
                        else:
                                anglefound = 180 + math.atan2(right-cY, (cols-1) - cX ) * (180/3.141592653589793)

                        bottlebyte = bytearray(struct.pack("i", int(anglefound)))
                        bottlebyte += (bytearray(struct.pack("i", int(cX))))
                        bottlebyte += (bytearray(struct.pack("i", int(cY))))
                        bottleSock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                        bottleSock.sendto(bottlebyte, (UDP_IP_ADDRESS, udp1))
                        
##                        anglebyte= bytearray(struct.pack("i", int(anglefound)))
##                        centerXbyte = bytearray(struct.pack("i", int(cX)))
##                        centerYbyte = bytearray(struct.pack("i", int(cY)))
                       
##                        angleSock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
##                        angleSock.sendto(anglebyte, (UDP_IP_ADDRESS, udp1))
##                        centerXSock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
##                        centerXSock.sendto(centerXbyte, (UDP_IP_ADDRESS, udp2))
##                        centerYSock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
##                        centerYSock.sendto(centerYbyte, (UDP_IP_ADDRESS, udp3))
                        

                        print('Angle found: ', anglefound)
                        print('Center X coordinate: ', cX)
                        print('Center Y coordinate: ', cY)
        
        

        cv2.imshow("mage", image)
        cv2.imshow("mask", mask)

        RecieverSock.setblocking(0)
        try:
            data, address = RecieverSock.recvfrom(1024)
            phase = int.from_bytes(data,byteorder='little')
        except socket.error:
            phase = 2

        if cv2.waitKey(1) & 0xFF == ord('q'):
            maincam.release()
            cv2.destroyAllWindows()
            break
	
cv2.destroyAllWindows()
    
